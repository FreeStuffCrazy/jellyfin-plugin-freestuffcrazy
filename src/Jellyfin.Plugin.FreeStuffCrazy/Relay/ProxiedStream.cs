using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.FreeStuffCrazy.Relay;

/// <summary>Where a stream sends its frames: the relay socket, or a test double.</summary>
internal interface IFrameSink
{
    ValueTask SendAsync(byte[] frame, CancellationToken cancellationToken);
}

/// <summary>The JSON payload of an OPEN frame.</summary>
internal sealed record OpenRequest(
    [property: JsonPropertyName("method")] string Method,
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("headers")] string[][]? Headers,
    [property: JsonPropertyName("window")] long Window);

/// <summary>
/// One relayed request: buffers the request body until END, forwards it to
/// this server, and streams the response back within the relay's window.
/// </summary>
internal sealed class ProxiedStream
{
    public const int MaxRequestBody = 1024 * 1024;

    // Waiting for Jellyfin to start answering; once it does, a response may
    // stream for as long as the viewer keeps reading.
    private static readonly TimeSpan HeadersTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan BodyTimeout = TimeSpan.FromSeconds(30);

    private readonly uint _id;
    private readonly OpenRequest _request;
    private readonly Uri _serverBase;
    private readonly HttpClient _http;
    private readonly IFrameSink _sink;
    private readonly FlowWindow _window;
    private readonly CancellationTokenSource _cancel;
    private readonly MemoryStream _body = new();
    private readonly object _bodyLock = new();
    private bool _bodyEnded;
    private bool _bodyDisposed;
    private readonly TaskCompletionSource _bodyComplete = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public ProxiedStream(uint id, OpenRequest request, Uri serverBase, HttpClient http, IFrameSink sink, CancellationToken connectionClosed)
    {
        _id = id;
        _request = request;
        _serverBase = serverBase;
        _http = http;
        _sink = sink;
        _window = new FlowWindow(request.Window);
        _cancel = CancellationTokenSource.CreateLinkedTokenSource(connectionClosed);
    }

    public bool BodyTooLarge { get; private set; }

    public void DisposeUnused()
    {
        DisposeBody();
        _cancel.Dispose();
    }

    public void AppendBody(ReadOnlySpan<byte> bytes)
    {
        lock (_bodyLock)
        {
            if (_bodyEnded || _bodyDisposed)
            {
                return;
            }

            if (_body.Length + bytes.Length > MaxRequestBody)
            {
                BodyTooLarge = true;
                return;
            }

            _body.Write(bytes);
        }
    }

    public void CompleteBody()
    {
        lock (_bodyLock)
        {
            if (_bodyEnded || _bodyDisposed)
            {
                return;
            }

            _bodyEnded = true;
            _bodyComplete.TrySetResult();
        }
    }

    private void DisposeBody()
    {
        lock (_bodyLock)
        {
            _bodyDisposed = true;
            _body.Dispose();
        }
    }

    public void Grant(uint bytes) => _window.Grant(bytes);

    /// <summary>The relay cancelled the stream, or the connection closed.</summary>
    public void Cancel()
    {
        _bodyComplete.TrySetCanceled();
        try
        {
            _cancel.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Cancelled after the stream finished and was disposed; nothing left to cancel.
        }
    }

    public async Task RunAsync()
    {
        var token = _cancel.Token;
        var headersSent = false;
        try
        {
            if (!ForwardPolicy.IsAllowed(_request.Method, _request.Path))
            {
                await SendErrorAsync(HttpStatusCode.Forbidden, "Not a FreeStuffCrazy Player request", token).ConfigureAwait(false);
                return;
            }

            try
            {
                await _bodyComplete.Task.WaitAsync(BodyTimeout, token).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                await SendErrorAsync(HttpStatusCode.RequestTimeout, "Request body timed out", token).ConfigureAwait(false);
                return;
            }
            if (BodyTooLarge)
            {
                await SendErrorAsync(HttpStatusCode.RequestEntityTooLarge, "Request body too large", token).ConfigureAwait(false);
                return;
            }

            using var message = BuildRequest();
            using var headersTimeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            headersTimeout.CancelAfter(HeadersTimeout);
            HttpResponseMessage response;
            try
            {
                response = await _http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, headersTimeout.Token).ConfigureAwait(false);
            }
            catch (Exception) when (!token.IsCancellationRequested)
            {
                await SendErrorAsync(HttpStatusCode.BadGateway, "The Jellyfin server did not answer", token).ConfigureAwait(false);
                return;
            }

            using (response)
            {
                await _sink.SendAsync(Frame.Encode(FrameType.Headers, _id, JsonSerializer.SerializeToUtf8Bytes(ResponseHead(response))), token).ConfigureAwait(false);
                headersSent = true;

                var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
                await using (stream.ConfigureAwait(false))
                {
                    var buffer = new byte[Frame.MaxDataLength];
                    while (true)
                    {
                        var read = await stream.ReadAsync(buffer, token).ConfigureAwait(false);
                        if (read == 0)
                        {
                            break;
                        }

                        var sent = 0;
                        while (sent < read)
                        {
                            var credit = await _window.TakeAsync(read - sent, token).ConfigureAwait(false);
                            await _sink.SendAsync(Frame.Encode(FrameType.Data, _id, buffer.AsSpan(sent, credit)), token).ConfigureAwait(false);
                            sent += credit;
                        }
                    }
                }
            }

            await _sink.SendAsync(Frame.Encode(FrameType.End, _id), token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Cancelled by the relay or the connection closed: nothing to tell the relay.
        }
        catch (Exception) when (!token.IsCancellationRequested)
        {
            // A malformed request or a broken response must not leave the relay waiting.
            if (headersSent)
            {
                await TrySendAsync(Frame.Encode(FrameType.Cancel, _id)).ConfigureAwait(false);
            }
            else
            {
                await TrySendAsync(Frame.Encode(FrameType.Error, _id, JsonSerializer.SerializeToUtf8Bytes(new { status = 400, message = "Request could not be forwarded" }))).ConfigureAwait(false);
            }
        }
        finally
        {
            DisposeBody();
            _cancel.Dispose();
        }
    }

    private HttpRequestMessage BuildRequest()
    {
        var target = new Uri(_serverBase.AbsoluteUri.TrimEnd('/') + _request.Path, UriKind.Absolute);
        var message = new HttpRequestMessage(new HttpMethod(_request.Method), target);
        if (_body.Length > 0)
        {
            message.Content = new ByteArrayContent(_body.ToArray());
        }

        foreach (var pair in _request.Headers ?? [])
        {
            if (pair is not [var name, var value] || name is null || value is null || !ForwardPolicy.IsForwardedRequestHeader(name) || value.Contains('\r') || value.Contains('\n'))
            {
                continue;
            }

            if (string.Equals(name, "Content-Type", StringComparison.OrdinalIgnoreCase))
            {
                message.Content?.Headers.TryAddWithoutValidation(name, value);
            }
            else
            {
                message.Headers.TryAddWithoutValidation(name, value);
            }
        }

        return message;
    }

    private static object ResponseHead(HttpResponseMessage response)
    {
        var headers = new List<string[]>();
        foreach (var (name, values) in response.Headers.Concat(response.Content.Headers))
        {
            if (ForwardPolicy.IsReturnedResponseHeader(name))
            {
                headers.AddRange(values.Select(value => new[] { name, value }));
            }
        }

        return new { status = (int)response.StatusCode, headers };
    }

    private ValueTask SendErrorAsync(HttpStatusCode status, string reason, CancellationToken token) =>
        _sink.SendAsync(Frame.Encode(FrameType.Error, _id, JsonSerializer.SerializeToUtf8Bytes(new { status = (int)status, message = reason })), token);

    private async Task TrySendAsync(byte[] frame)
    {
        try
        {
            await _sink.SendAsync(frame, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // The send failed because the connection is gone; there is no one to tell.
        }
    }
}
