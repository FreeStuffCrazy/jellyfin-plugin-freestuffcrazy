namespace Jellyfin.Plugin.FreeStuffCrazy.Relay;

/// <summary>Where the connection to the relay stands, as shown on the plugin page.</summary>
public enum ConnectorStatus
{
    Disabled,
    Connecting,
    WaitingForLink,
    Linked,
    Offline,
    UpdateRequired,
}

/// <summary>
/// The connector's current status, shared between the relay connection and the
/// plugin page's API. Also lets the page ask the connection to start over.
/// </summary>
public sealed class ConnectorState
{
    private readonly object _lock = new();
    private CancellationTokenSource _restart = new();

    public ConnectorStatus Status { get; private set; } = ConnectorStatus.Connecting;

    public string? PairingUrl { get; private set; }

    public string? PairingCode { get; private set; }

    public DateTimeOffset? PairingExpiresAt { get; private set; }

    public string? Label { get; private set; }

    public string? Message { get; private set; }

    public DateTimeOffset? ConnectedSince { get; private set; }

    public int ActiveStreams { get; private set; }

    /// <summary>Cancelled when the plugin page asks the connection to start over.</summary>
    public CancellationToken RestartToken
    {
        get
        {
            lock (_lock)
            {
                return _restart.Token;
            }
        }
    }

    public void RequestRestart()
    {
        CancellationTokenSource old;
        lock (_lock)
        {
            old = _restart;
            _restart = new CancellationTokenSource();
        }

        old.Cancel();
        old.Dispose();
    }

    public void Set(ConnectorStatus status, string? message = null)
    {
        lock (_lock)
        {
            Status = status;
            Message = message;
            if (status != ConnectorStatus.WaitingForLink)
            {
                PairingUrl = PairingCode = null;
                PairingExpiresAt = null;
            }

            if (status != ConnectorStatus.Linked)
            {
                ConnectedSince = null;
                ActiveStreams = 0;
            }
        }
    }

    public void SetPairing(string code, string url, DateTimeOffset expiresAt)
    {
        lock (_lock)
        {
            Status = ConnectorStatus.WaitingForLink;
            Message = null;
            PairingCode = code;
            PairingUrl = url;
            PairingExpiresAt = expiresAt;
        }
    }

    public void SetLinked(string? label)
    {
        lock (_lock)
        {
            Status = ConnectorStatus.Linked;
            Message = null;
            Label = label;
            ConnectedSince ??= DateTimeOffset.UtcNow;
        }
    }

    public void SetActiveStreams(int count)
    {
        lock (_lock)
        {
            ActiveStreams = count;
        }
    }
}
