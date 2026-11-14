namespace Jellyfin.Plugin.FreeStuffCrazy.Relay;

/// <summary>
/// The response bytes a stream may still send before the relay grants more
/// (WINDOW frames). Sending waits here, which slows the read from Jellyfin to
/// the pace of the viewer.
/// </summary>
internal sealed class FlowWindow
{
    private readonly SemaphoreSlim _granted = new(0);
    private long _available;

    public FlowWindow(long initial) => _available = Math.Max(0, initial);

    public long Available => Interlocked.Read(ref _available);

    public void Grant(long bytes)
    {
        if (bytes <= 0)
        {
            return;
        }

        while (true)
        {
            var available = Interlocked.Read(ref _available);
            var updated = bytes >= long.MaxValue - available ? long.MaxValue : available + bytes;
            if (Interlocked.CompareExchange(ref _available, updated, available) != available)
            {
                continue;
            }

            if (available == 0)
            {
                _granted.Release();
            }

            return;
        }
    }

    /// <summary>Takes between 1 and <paramref name="wanted"/> bytes of credit, waiting for a grant if none is left.</summary>
    public async Task<int> TakeAsync(int wanted, CancellationToken cancellationToken)
    {
        while (true)
        {
            var available = Interlocked.Read(ref _available);
            if (available > 0)
            {
                var take = (int)Math.Min(available, wanted);
                if (Interlocked.CompareExchange(ref _available, available - take, available) == available)
                {
                    return take;
                }

                continue;
            }

            await _granted.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }
}
