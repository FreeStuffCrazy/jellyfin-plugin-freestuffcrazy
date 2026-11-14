using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Jellyfin.Plugin.FreeStuffCrazy.Relay;
using Xunit;

namespace Jellyfin.Plugin.FreeStuffCrazy.Tests;

public class FrameTests
{
    [Fact]
    public void RoundTrips()
    {
        var bytes = Frame.Encode(FrameType.Data, 0x01020304, [9, 8, 7]);
        Assert.Equal(new byte[] { 3, 1, 2, 3, 4, 9, 8, 7 }, bytes);
        Assert.True(Frame.TryDecode(bytes, out var frame));
        Assert.Equal(FrameType.Data, frame.Type);
        Assert.Equal(0x01020304u, frame.Stream);
        Assert.Equal(new byte[] { 9, 8, 7 }, frame.Payload.ToArray());
    }

    [Fact]
    public void ReadsWindowGrants()
    {
        Assert.True(Frame.TryDecode(Frame.EncodeWindow(5, 262144), out var frame));
        Assert.Equal(262144u, frame.WindowBytes());
    }

    [Fact]
    public void RejectsShortOrUnknownFrames()
    {
        Assert.False(Frame.TryDecode(new byte[] { 3, 0, 0 }, out _));
        Assert.False(Frame.TryDecode(new byte[] { 42, 0, 0, 0, 1 }, out _));
    }
}

public class FlowWindowTests
{
    [Fact]
    public async Task GrantsSaturateInsteadOfOverflowing()
    {
        var window = new FlowWindow(long.MaxValue - 1);
        window.Grant(uint.MaxValue);
        Assert.Equal(long.MaxValue, window.Available);
        Assert.Equal(64, await window.TakeAsync(64, CancellationToken.None));
    }

    [Fact]
    public async Task WaitsForAGrantOnceTheWindowIsUsed()
    {
        var window = new FlowWindow(10);
        Assert.Equal(10, await window.TakeAsync(64, CancellationToken.None));
        var waiting = window.TakeAsync(64, CancellationToken.None);
        await Task.Delay(50);
        Assert.False(waiting.IsCompleted);
        window.Grant(5);
        Assert.Equal(5, await waiting.WaitAsync(TimeSpan.FromSeconds(5)));
    }
}

public class ProxiedStreamTests
{
    private const string Item = "0123456789abcdef0123456789abcdef";

    [Fact]
    public async Task ForwardsAnAllowedRequestAndStreamsTheResponseWithinTheWindow()
    {
        var body = new byte[150_000];
        Random.Shared.NextBytes(body);
        HttpRequestMessage? seen = null;
        using var server = new HttpClient(new FakeServer(request =>
        {
            seen = request;
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) };
            response.Content.Headers.TryAddWithoutValidation("Content-Type", "video/mp2t");
            response.Headers.TryAddWithoutValidation("Set-Cookie", "secret=1");
            return response;
        }));
        var sink = new RecordingSink();
        var request = new OpenRequest("GET", $"/Videos/{Item}/hls1/main/3.ts?PlaySessionId=p", [["Authorization", "MediaBrowser Token=\"t\""], ["Cookie", "c=1"], ["Host", "evil"]], 100_000);
        var stream = new ProxiedStream(7, request, new Uri("http://127.0.0.1:8096/"), server, sink, CancellationToken.None);
        stream.CompleteBody();

        var run = stream.RunAsync();
        // The first 100,000 bytes may go out; the rest waits for a WINDOW grant.
        await sink.WaitForBytesAsync(100_000);
        await Task.Delay(100);
        Assert.Equal(100_000, sink.DataBytes);
        stream.Grant(100_000);
        await run.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal($"http://127.0.0.1:8096/Videos/{Item}/hls1/main/3.ts?PlaySessionId=p", seen!.RequestUri!.ToString());
        Assert.True(seen.Headers.Contains("Authorization"));
        Assert.False(seen.Headers.Contains("Cookie"));
        Assert.Equal("127.0.0.1:8096", seen.Headers.Host ?? "127.0.0.1:8096");

        Assert.Equal(FrameType.Headers, sink.Frames[0].Type);
        using var head = JsonDocument.Parse(sink.Frames[0].Payload);
        Assert.Equal(200, head.RootElement.GetProperty("status").GetInt32());
        var names = head.RootElement.GetProperty("headers").EnumerateArray().Select(h => h[0].GetString()).ToList();
        Assert.Contains("Content-Type", names);
        Assert.DoesNotContain("Set-Cookie", names);
        Assert.Equal(body, sink.Frames.Where(f => f.Type == FrameType.Data).SelectMany(f => f.Payload).ToArray());
        Assert.All(sink.Frames.Where(f => f.Type == FrameType.Data), f => Assert.True(f.Payload.Length <= Frame.MaxDataLength));
        Assert.Equal(FrameType.End, sink.Frames[^1].Type);
    }

    [Fact]
    public async Task EndsWhenResponseExactlyUsesWindow()
    {
        using var server = new HttpClient(new FakeServer(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[16]) }));
        var sink = new RecordingSink();
        var stream = new ProxiedStream(8, new OpenRequest("GET", "/System/Info/Public", null, 16), new Uri("http://127.0.0.1:8096/"), server, sink, CancellationToken.None);
        stream.CompleteBody();
        await stream.RunAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(FrameType.End, sink.Frames[^1].Type);
    }

    [Fact]
    public async Task RefusesAPathOutsideThePolicyWithoutContactingTheServer()
    {
        var called = false;
        using var server = new HttpClient(new FakeServer(_ => { called = true; return new HttpResponseMessage(HttpStatusCode.OK); }));
        var sink = new RecordingSink();
        var stream = new ProxiedStream(9, new OpenRequest("GET", "/System/Configuration", null, 1000), new Uri("http://127.0.0.1:8096/"), server, sink, CancellationToken.None);
        stream.CompleteBody();
        await stream.RunAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(called);
        var frame = Assert.Single(sink.Frames);
        Assert.Equal(FrameType.Error, frame.Type);
        using var error = JsonDocument.Parse(frame.Payload);
        Assert.Equal(403, error.RootElement.GetProperty("status").GetInt32());
    }

    [Fact]
    public async Task IgnoresBodyFramesAfterEndAndDisposal()
    {
        using var server = new HttpClient(new FakeServer(request =>
        {
            Assert.Equal("first", request.Content!.ReadAsStringAsync().Result);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([]) };
        }));
        var sink = new RecordingSink();
        var stream = new ProxiedStream(12, new OpenRequest("POST", "/Users/AuthenticateByName", null, 0), new Uri("http://127.0.0.1:8096/"), server, sink, CancellationToken.None);
        stream.AppendBody(Encoding.UTF8.GetBytes("first"));
        stream.CompleteBody();
        stream.AppendBody(Encoding.UTF8.GetBytes("late"));
        await stream.RunAsync().WaitAsync(TimeSpan.FromSeconds(5));
        stream.AppendBody(Encoding.UTF8.GetBytes("later"));
        Assert.Equal(FrameType.End, sink.Frames[^1].Type);
    }

    [Fact]
    public async Task IgnoresNullHeaderValues()
    {
        using var server = new HttpClient(new FakeServer(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([]) }));
        var sink = new RecordingSink();
        var stream = new ProxiedStream(10, new OpenRequest("GET", "/System/Info/Public", [["Authorization", null!]], 0), new Uri("http://127.0.0.1:8096/"), server, sink, CancellationToken.None);
        stream.CompleteBody();
        await stream.RunAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(FrameType.End, sink.Frames[^1].Type);
    }

    [Fact]
    public async Task SendsTheRequestBodyAndReportsAnUnreachableServer()
    {
        string? received = null;
        using var server = new HttpClient(new FakeServer(request =>
        {
            received = request.Content!.ReadAsStringAsync().Result;
            throw new HttpRequestException("connection refused");
        }));
        var sink = new RecordingSink();
        var stream = new ProxiedStream(11, new OpenRequest("POST", "/Users/AuthenticateByName", [["Content-Type", "application/json"]], 1000), new Uri("http://127.0.0.1:8096/"), server, sink, CancellationToken.None);
        stream.AppendBody(Encoding.UTF8.GetBytes("{\"Username\":\"pat\"}"));
        stream.CompleteBody();
        await stream.RunAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("{\"Username\":\"pat\"}", received);
        var frame = Assert.Single(sink.Frames);
        Assert.Equal(FrameType.Error, frame.Type);
        using var error = JsonDocument.Parse(frame.Payload);
        Assert.Equal(502, error.RootElement.GetProperty("status").GetInt32());
    }

    [Fact]
    public async Task CancellingStopsTheStreamQuietly()
    {
        using var server = new HttpClient(new FakeServer(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new EndlessStream()) }));
        var sink = new RecordingSink();
        var stream = new ProxiedStream(13, new OpenRequest("GET", "/Videos/" + Item + "/master.m3u8", null, long.MaxValue), new Uri("http://127.0.0.1:8096/"), server, sink, CancellationToken.None);
        stream.CompleteBody();
        var run = stream.RunAsync();
        await sink.WaitForBytesAsync(1);
        stream.Cancel();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.DoesNotContain(sink.Frames, f => f.Type == FrameType.End);
    }

    private sealed class FakeServer(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }

    private sealed class EndlessStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) { Thread.Sleep(1); return count; }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class RecordingSink : IFrameSink
    {
        private readonly Channel<int> _progress = Channel.CreateUnbounded<int>();

        public List<(FrameType Type, byte[] Payload)> Frames { get; } = [];

        public int DataBytes { get; private set; }

        public ValueTask SendAsync(byte[] frame, CancellationToken cancellationToken)
        {
            Assert.True(Frame.TryDecode(frame, out var decoded));
            lock (Frames)
            {
                Frames.Add((decoded.Type, decoded.Payload.ToArray()));
                if (decoded.Type == FrameType.Data)
                {
                    DataBytes += decoded.Payload.Length;
                }
            }

            _progress.Writer.TryWrite(DataBytes);
            return ValueTask.CompletedTask;
        }

        public async Task WaitForBytesAsync(int bytes)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (DataBytes < bytes)
            {
                await _progress.Reader.ReadAsync(timeout.Token);
            }
        }
    }
}

public class RelayEndpointTests
{
    [Fact]
    public async Task UsesTheProductionRelayByDefault()
    {
        var (uri, error) = await RelayEndpoint.ResolveAsync(null, CancellationToken.None);
        Assert.Null(error);
        Assert.Equal("wss://relay.freestuffcrazy.com/connect", uri!.ToString());
    }

    [Theory]
    [InlineData("ws://127.0.0.1:3000/connect", "ws://127.0.0.1:3000/connect")]
    [InlineData("ws://192.168.0.10:3000/connect", "ws://192.168.0.10:3000/connect")]
    [InlineData("ws://localhost:3000/connect", null)]
    public async Task AcceptsALocalDevRelay(string address, string? expected)
    {
        var (uri, error) = await RelayEndpoint.ResolveAsync(address, CancellationToken.None);
        Assert.Null(error);
        Assert.True(uri!.Host is "127.0.0.1" or "[::1]" or "192.168.0.10");
        if (expected is not null)
        {
            Assert.Equal(expected, uri.ToString());
        }
    }

    [Theory]
    [InlineData("ws://93.184.216.34/connect")] // public address
    [InlineData("ws://100.64.0.1/connect")] // carrier-grade NAT space is not counted as local
    [InlineData("https://127.0.0.1/connect")] // not a WebSocket address
    [InlineData("ws://user:pass@127.0.0.1/connect")]
    [InlineData("not a url")]
    public async Task RefusesAnythingElse(string address)
    {
        var (uri, error) = await RelayEndpoint.ResolveAsync(address, CancellationToken.None);
        Assert.Null(uri);
        Assert.NotNull(error);
    }
}

public class PairingUrlTests
{
    [Theory]
    [InlineData("https://player.freestuffcrazy.com/link?code=123", true)]
    [InlineData("javascript:alert(1)", false)]
    [InlineData("https://evil.example/link", false)]
    [InlineData("https://player.freestuffcrazy.com.evil.example/link", false)]
    [InlineData("http://player.freestuffcrazy.com/link", false)]
    [InlineData("https://player.freestuffcrazy.com:444/link", false)]
    public void ValidatesPairingDestination(string url, bool expected) =>
        Assert.Equal(expected, RelayClient.IsValidPairingUrl(url));
}
