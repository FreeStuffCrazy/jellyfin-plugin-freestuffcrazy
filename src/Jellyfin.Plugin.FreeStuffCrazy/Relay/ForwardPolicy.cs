using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.FreeStuffCrazy.Relay;

/// <summary>
/// What the connector is willing to forward. Relayed requests reach Jellyfin
/// from this machine, so Jellyfin treats them as local network traffic; only
/// the endpoints FreeStuffCrazy Player uses are let through. See PROTOCOL.md.
/// </summary>
internal static partial class ForwardPolicy
{
    private const string Id = "[0-9a-f]{8}-?[0-9a-f]{4}-?[0-9a-f]{4}-?[0-9a-f]{4}-?[0-9a-f]{12}";

    private static readonly (string Method, Regex Path)[] Allowed =
    [
        ("GET", Rx("/System/Info/Public")),
        ("GET", Rx("/QuickConnect/(?:Enabled|Connect)")),
        ("POST", Rx("/QuickConnect/(?:Initiate|Authorize)")),
        ("POST", Rx("/Users/(?:AuthenticateByName|AuthenticateWithQuickConnect)")),
        ("GET", Rx("/Users/Me")),
        ("GET", Rx("/UserViews")),
        ("GET", Rx("/UserItems/Resume")),
        ("GET", Rx("/Items")),
        ("GET", Rx("/Items/Latest")),
        ("GET", Rx($"/Items/{Id}")),
        ("POST", Rx($"/Items/{Id}/PlaybackInfo")),
        ("GET", Rx($"/Items/{Id}/Images/(?:Primary|Backdrop|Thumb)(?:/[0-9]{{1,3}})?")),
        ("GET", Rx("/Shows/NextUp")),
        ("GET", Rx($"/Shows/{Id}/(?:Seasons|Episodes)")),
        ("POST", Rx($"/UserPlayedItems/{Id}")),
        ("DELETE", Rx($"/UserPlayedItems/{Id}")),
        ("POST", Rx("/Sessions/Playing(?:/Progress|/Stopped)?")),
        ("POST", Rx("/Sessions/Logout")),
        ("DELETE", Rx("/Videos/ActiveEncodings")),
        ("GET", Rx($"/Videos/{Id}/(?:master|main)\\.m3u8")),
        ("GET", Rx($"/Videos/{Id}/hls1/[A-Za-z0-9_-]{{1,64}}/[0-9]{{1,7}}\\.(?:ts|mp4|m4s)")),
    ];

    private static readonly HashSet<string> RequestHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Accept", "Accept-Language", "Authorization", "Content-Type", "If-Modified-Since",
        "If-None-Match", "Range", "User-Agent", "X-Emby-Authorization",
    };

    private static readonly HashSet<string> ResponseHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Accept-Ranges", "Cache-Control", "Content-Length", "Content-Range", "Content-Type",
        "ETag", "Last-Modified",
    };

    /// <summary>
    /// Checks a relayed request line. On success, <paramref name="pathAndQuery"/>
    /// is safe to append to the server's own base URL.
    /// </summary>
    public static bool IsAllowed(string method, string pathAndQuery)
    {
        if (string.IsNullOrEmpty(pathAndQuery) || pathAndQuery.Length > 6144 || pathAndQuery[0] != '/')
        {
            return false;
        }

        foreach (var c in pathAndQuery)
        {
            if (c < 0x21 || c > 0x7e)
            {
                return false; // control characters, spaces, non-ASCII
            }
        }

        var queryStart = pathAndQuery.IndexOf('?', StringComparison.Ordinal);
        var path = queryStart < 0 ? pathAndQuery : pathAndQuery[..queryStart];
        if (path.Length > 2048 || path.Contains("//", StringComparison.Ordinal) || path.Contains('\\', StringComparison.Ordinal)
            || path.Contains('%', StringComparison.Ordinal) || path.Contains("..", StringComparison.Ordinal)
            || path.Contains('#', StringComparison.Ordinal))
        {
            return false;
        }

        if (queryStart >= 0 && pathAndQuery.Contains('#', StringComparison.Ordinal))
        {
            return false;
        }

        foreach (var (allowedMethod, pattern) in Allowed)
        {
            if (string.Equals(method, allowedMethod, StringComparison.Ordinal) && pattern.IsMatch(path))
            {
                return true;
            }
        }

        return false;
    }

    public static bool IsForwardedRequestHeader(string name) => RequestHeaders.Contains(name);

    public static bool IsReturnedResponseHeader(string name) => ResponseHeaders.Contains(name);

    private static Regex Rx(string path) =>
        new($"^{path}$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50));
}
