using System.Collections.Concurrent;
using System.Net;
using System.Net.WebSockets;
using System.Text.Json;
using MediaBrowser.Controller;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.FreeStuffCrazy.Relay;

/// <summary>
/// Keeps this server's connection to the FreeStuffCrazy Player relay open and
/// serves the requests that arrive on it. See PROTOCOL.md.
/// </summary>
internal sealed class RelayClient : BackgroundService, IFrameSink
{
    private const string SubProtocol = "fsc-relay.1";
    private const int ProtocolVersion = 1;
    private const int MaxMessage = 256 * 1024;
    private const int MaxStreams = 32;
    private static readonly TimeSpan IdleLimit = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(60);

    private readonly ILogger<RelayClient> _logger;
    private readonly IServerApplicationHost _host;
    private readonly ConnectorState _state;
    private readonly HttpClient _http;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly ConcurrentDictionary<uint, ProxiedStream> _streams = new();
    private ClientWebSocket? _socket;
    private long _lastHeardTicks;

    public RelayClient(ILogger<RelayClient> logger, IServerApplicationHost host, ConnectorState state)
    {
        _logger = logger;
        _host = host;
        _state = state;
        // Talks only to this server: no proxy, no redirects, no decompression
        // (bytes pass through as Jellyfin sent them).
        _http = new HttpClient(new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseProxy = false,
            UseCookies = false,
            AutomaticDecompression = DecompressionMethods.None,
            PooledConnectionIdleTimeout = TimeSpan.FromSeconds(30),
        })
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
    }

    public async ValueTask SendAsync(byte[] frame, CancellationToken cancellationToken)
    {
        var socket = _socket ?? throw new WebSocketException("Not connected");
        await _sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await socket.SendAsync(frame, WebSocketMessageType.Binary, true, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!_host.CoreStartupHasCompleted && !stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken).ConfigureAwait(false);
        }

        var backoff = TimeSpan.FromSeconds(1);
        while (!stoppingToken.IsCancellationRequested)
        {
            using var session = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, _state.RestartToken);
            var config = Plugin.Instance?.Configuration;
            if (config is null || !config.Enabled)
            {
                _state.Set(ConnectorStatus.Disabled);
                await WaitAsync(Timeout.InfiniteTimeSpan, session.Token).ConfigureAwait(false);
                continue;
            }

            var started = DateTimeOffset.UtcNow;
            var stop = false;
            try
            {
                stop = await RunSessionAsync(session.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is WebSocketException or HttpRequestException or IOException or JsonException)
            {
                _logger.LogInformation("FreeStuffCrazy Player relay connection ended: {Message}", ex.Message);
                _state.Set(ConnectorStatus.Offline, "Can't reach FreeStuffCrazy Player right now. Retrying.");
            }
            catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
            {
                // Restart requested from the plugin page, or the connection went quiet.
                if (!session.IsCancellationRequested)
                {
                    _state.Set(ConnectorStatus.Offline, "The connection went quiet. Reconnecting.");
                }
            }
            finally
            {
                CancelAllStreams();
                _socket?.Dispose();
                _socket = null;
            }

            if (stop)
            {
                await WaitAsync(Timeout.InfiniteTimeSpan, _state.RestartToken, stoppingToken).ConfigureAwait(false);
                continue;
            }

            // The session token is cancelled only by shutdown or a restart from the plugin page.
            if (session.IsCancellationRequested && !stoppingToken.IsCancellationRequested)
            {
                backoff = TimeSpan.FromSeconds(1);
                continue; // restart right away
            }

            if (DateTimeOffset.UtcNow - started > TimeSpan.FromSeconds(60))
            {
                backoff = TimeSpan.FromSeconds(1);
            }

            // Full jitter, so many servers don't reconnect in lockstep after a relay restart.
            await WaitAsync(TimeSpan.FromMilliseconds(Random.Shared.NextDouble() * backoff.TotalMilliseconds), _state.RestartToken, stoppingToken).ConfigureAwait(false);
            backoff = TimeSpan.FromTicks(Math.Min(backoff.Ticks * 2, MaxBackoff.Ticks));
        }
    }

    /// <summary>One connection, start to finish. True means stop until the plugin page restarts it.</summary>
    private async Task<bool> RunSessionAsync(CancellationToken token)
    {
        _state.Set(ConnectorStatus.Connecting);
        var devOverride = Environment.GetEnvironmentVariable(RelayEndpoint.DevOverrideVariable);
        if (string.IsNullOrWhiteSpace(devOverride))
        {
            devOverride = Plugin.Instance?.Configuration.DevRelayUrl;
        }

        var (relay, error) = await RelayEndpoint.ResolveAsync(devOverride, token).ConfigureAwait(false);
        if (relay is null)
        {
            _state.Set(ConnectorStatus.Offline, error);
            return true;
        }

        var socket = new ClientWebSocket();
        socket.Options.AddSubProtocol(SubProtocol);
        socket.Options.KeepAliveInterval = TimeSpan.Zero; // the relay's ping/pong is the liveness check
        _socket = socket;
        using (var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(token))
        {
            connectTimeout.CancelAfter(TimeSpan.FromSeconds(15));
            await socket.ConnectAsync(relay, connectTimeout.Token).ConfigureAwait(false);
        }

        Heard();
        await SendControlAsync(Hello(), token).ConfigureAwait(false);

        using var quiet = CancellationTokenSource.CreateLinkedTokenSource(token);
        var watchdog = WatchForSilenceAsync(quiet);
        try
        {
            return await ReceiveLoopAsync(socket, quiet.Token).ConfigureAwait(false);
        }
        finally
        {
            await quiet.CancelAsync().ConfigureAwait(false);
            await watchdog.ConfigureAwait(false);
        }
    }

    private async Task<bool> ReceiveLoopAsync(ClientWebSocket socket, CancellationToken token)
    {
        var buffer = new byte[MaxMessage];
        while (true)
        {
            var length = 0;
            ValueWebSocketReceiveResult result;
            do
            {
                if (length == buffer.Length)
                {
                    throw new WebSocketException("Relay message too large");
                }

                result = await socket.ReceiveAsync(buffer.AsMemory(length), token).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    return HandleClose(socket.CloseStatus, socket.CloseStatusDescription);
                }

                length += result.Count;
            }
            while (!result.EndOfMessage);

            Heard();
            if (result.MessageType != WebSocketMessageType.Binary || !Frame.TryDecode(buffer.AsMemory(0, length).ToArray(), out var frame))
            {
                continue; // ignore what this version doesn't understand
            }

            if (await HandleFrameAsync(frame, token).ConfigureAwait(false))
            {
                await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, token).ConfigureAwait(false);
                return false;
            }
        }
    }

    /// <summary>Handles one frame. True means close this connection and reconnect.</summary>
    private async Task<bool> HandleFrameAsync(Frame frame, CancellationToken token)
    {
        switch (frame.Type)
        {
            case FrameType.Control:
                return await HandleControlAsync(frame.Payload, token).ConfigureAwait(false);

            case FrameType.Open:
                OpenStream(frame);
                break;

            case FrameType.Data when _streams.TryGetValue(frame.Stream, out var stream):
                stream.AppendBody(frame.Payload.Span);
                break;

            case FrameType.End when _streams.TryGetValue(frame.Stream, out var stream):
                stream.CompleteBody();
                break;

            case FrameType.Window when _streams.TryGetValue(frame.Stream, out var stream):
                stream.Grant(frame.WindowBytes());
                break;

            case FrameType.Cancel when _streams.TryRemove(frame.Stream, out var stream):
                stream.Cancel();
                _state.SetActiveStreams(_streams.Count);
                break;
        }

        return false;
    }

    private void OpenStream(Frame frame)
    {
        OpenRequest? request = null;
        try
        {
            request = JsonSerializer.Deserialize<OpenRequest>(frame.Payload.Span);
        }
        catch (JsonException)
        {
            // Answered below as a refused request.
        }

        if (frame.Stream == 0 || _streams.Count >= MaxStreams || _streams.ContainsKey(frame.Stream))
        {
            _ = SendAsync(
                Frame.Encode(FrameType.Error, frame.Stream, JsonSerializer.SerializeToUtf8Bytes(new { status = 503, message = "Too many requests at once" })),
                CancellationToken.None).AsTask().ContinueWith(_ => { }, TaskScheduler.Default);
            return;
        }

        var serverBase = new Uri(_host.GetApiUrlForLocalAccess(IPAddress.Loopback, allowHttps: false));
        var stream = new ProxiedStream(frame.Stream, request ?? new OpenRequest("INVALID", string.Empty, null, 0), serverBase, _http, this, CancellationToken.None);
        if (!_streams.TryAdd(frame.Stream, stream))
        {
            stream.DisposeUnused();
            _ = SendAsync(
                Frame.Encode(FrameType.Error, frame.Stream, JsonSerializer.SerializeToUtf8Bytes(new { status = 503, message = "Too many requests at once" })),
                CancellationToken.None).AsTask().ContinueWith(_ => { }, TaskScheduler.Default);
            return;
        }

        _state.SetActiveStreams(_streams.Count);
        _ = Task.Run(async () =>
        {
            try
            {
                await stream.RunAsync().ConfigureAwait(false);
            }
            finally
            {
                _streams.TryRemove(new KeyValuePair<uint, ProxiedStream>(frame.Stream, stream));
                _state.SetActiveStreams(_streams.Count);
            }
        });
    }

    private async Task<bool> HandleControlAsync(ReadOnlyMemory<byte> payload, CancellationToken token)
    {
        using var message = JsonDocument.Parse(payload);
        var root = message.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        string? Text(string name) => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

        switch (Text("type"))
        {
            case "ping":
                await SendControlAsync(new { type = "pong", t = root.TryGetProperty("t", out var t) ? t.Clone() : default(JsonElement?) }, token).ConfigureAwait(false);
                break;

            case "pairing":
                if (IsValidPairingUrl(Text("url")))
                {
                    _state.SetPairing(
                        Text("code") ?? string.Empty,
                        Text("url")!,
                        DateTimeOffset.TryParse(Text("expiresAt"), out var expires) ? expires : DateTimeOffset.UtcNow.AddMinutes(10));
                }
                else
                {
                    _logger.LogWarning("Relay supplied an invalid pairing URL");
                    _state.Set(ConnectorStatus.Offline, "The relay supplied an invalid pairing link.");
                }

                break;

            case "linked":
                if (Text("connectorId") is { Length: > 0 } linkedId && Text("secret") is { Length: > 0 } linkedSecret)
                {
                    SaveCredential(linkedId, linkedSecret);
                    await SendControlAsync(new { type = "saved", connectorId = linkedId }, token).ConfigureAwait(false);
                    _state.SetLinked(Text("label"));
                    _logger.LogInformation("Linked to FreeStuffCrazy Player as {Label}", Text("label"));
                }

                break;

            case "ready":
                _state.SetLinked(Text("label"));
                break;

            case "rotate":
                if (Text("secret") is { Length: > 0 } secret && Plugin.Instance?.Configuration.ConnectorId is { Length: > 0 } id)
                {
                    SaveCredential(id, secret);
                    await SendControlAsync(new { type = "saved", connectorId = id }, token).ConfigureAwait(false);
                }

                break;

            case "revoked":
                _logger.LogWarning("FreeStuffCrazy Player unlinked this server: {Reason}", Text("reason"));
                SaveCredential(string.Empty, string.Empty);
                return true; // reconnect unlinked for a new pairing link

            case "error":
                _state.Set(_state.Status, Text("message"));
                break;
        }

        return false;
    }

    internal static bool IsValidPairingUrl(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttps
        && string.Equals(uri.IdnHost, "player.freestuffcrazy.com", StringComparison.OrdinalIgnoreCase)
        && uri.IsDefaultPort
        && string.IsNullOrEmpty(uri.UserInfo)
        && string.IsNullOrEmpty(uri.Fragment)
        && string.Equals(uri.AbsolutePath, "/link", StringComparison.Ordinal);

    private bool HandleClose(WebSocketCloseStatus? status, string? description)
    {
        switch ((int?)status)
        {
            case 4001:
            case 4003:
                _logger.LogWarning("FreeStuffCrazy Player no longer accepts this server's link ({Code}); it must be linked again", (int)status!);
                SaveCredential(string.Empty, string.Empty);
                _state.Set(ConnectorStatus.Connecting);
                return false;

            case 4002:
                _state.Set(ConnectorStatus.UpdateRequired, "Update this plugin to keep using FreeStuffCrazy Player.");
                return true;

            default:
                _state.Set(ConnectorStatus.Offline, string.IsNullOrEmpty(description) ? "Disconnected from FreeStuffCrazy Player. Reconnecting." : description);
                return false;
        }
    }

    private object Hello()
    {
        var config = Plugin.Instance?.Configuration;
        var linked = !string.IsNullOrEmpty(config?.ConnectorId) && !string.IsNullOrEmpty(config?.Secret);
        return new
        {
            type = "hello",
            protocol = ProtocolVersion,
            plugin = Plugin.Instance?.Version.ToString(),
            jellyfin = _host.ApplicationVersionString,
            serverId = _host.SystemId,
            serverName = _host.FriendlyName,
            connectorId = linked ? config!.ConnectorId : null,
            secret = linked ? config!.Secret : null,
        };
    }

    private static void SaveCredential(string connectorId, string secret)
    {
        var plugin = Plugin.Instance;
        if (plugin is null)
        {
            return;
        }

        plugin.Configuration.ConnectorId = connectorId;
        plugin.Configuration.Secret = secret;
        plugin.SaveConfiguration();
    }

    private ValueTask SendControlAsync(object message, CancellationToken token) =>
        SendAsync(Frame.Encode(FrameType.Control, 0, JsonSerializer.SerializeToUtf8Bytes(message)), token);

    private void Heard() => Interlocked.Exchange(ref _lastHeardTicks, DateTime.UtcNow.Ticks);

    /// <summary>Ends the connection if the relay goes silent (it pings at least every 20 seconds).</summary>
    private async Task WatchForSilenceAsync(CancellationTokenSource connection)
    {
        try
        {
            while (!connection.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(5), connection.Token).ConfigureAwait(false);
                if (DateTime.UtcNow.Ticks - Interlocked.Read(ref _lastHeardTicks) > IdleLimit.Ticks)
                {
                    _socket?.Abort();
                    await connection.CancelAsync().ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // The connection already ended, so there is nothing left to watch.
        }
    }

    private void CancelAllStreams()
    {
        foreach (var id in _streams.Keys)
        {
            if (_streams.TryRemove(id, out var stream))
            {
                stream.Cancel();
            }
        }

        _state.SetActiveStreams(0);
    }

    private static async Task WaitAsync(TimeSpan delay, CancellationToken first, CancellationToken second = default)
    {
        using var either = CancellationTokenSource.CreateLinkedTokenSource(first, second);
        try
        {
            await Task.Delay(delay, either.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Woken early: restart requested or shutting down.
        }
    }

    public override void Dispose()
    {
        _http.Dispose();
        _sendLock.Dispose();
        _socket?.Dispose();
        base.Dispose();
    }
}
