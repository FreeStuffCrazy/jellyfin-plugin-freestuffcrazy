using System.Net;
using System.Net.Sockets;

namespace Jellyfin.Plugin.FreeStuffCrazy.Relay;

/// <summary>
/// The relay this connector talks to. It is fixed, because the relay is a hosted
/// service. For development, FREESTUFFCRAZY_DEV_RELAY (for example
/// ws://192.168.0.10:3000/connect) is honored only when every address its host
/// resolves to is local or private; the connection then goes to the address
/// that was checked.
/// </summary>
internal static class RelayEndpoint
{
    public static readonly Uri Production = new("wss://relay.freestuffcrazy.com/connect");

    public const string DevOverrideVariable = "FREESTUFFCRAZY_DEV_RELAY";

    /// <summary>The address to connect to, or an error for the plugin page.</summary>
    public static async Task<(Uri? Uri, string? Error)> ResolveAsync(string? devOverride, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(devOverride))
        {
            return (Production, null);
        }

        if (!Uri.TryCreate(devOverride.Trim(), UriKind.Absolute, out var uri) || (uri.Scheme != "ws" && uri.Scheme != "wss")
            || !string.IsNullOrEmpty(uri.UserInfo))
        {
            return (null, $"{DevOverrideVariable} must be a ws:// or wss:// address.");
        }

        IPAddress[] addresses;
        try
        {
            addresses = IPAddress.TryParse(uri.Host.Trim('[', ']'), out var literal)
                ? [literal]
                : await Dns.GetHostAddressesAsync(uri.DnsSafeHost, cancellationToken).ConfigureAwait(false);
        }
        catch (SocketException)
        {
            return (null, $"{DevOverrideVariable} names a host that could not be found.");
        }

        if (addresses.Length == 0 || !addresses.All(IsLocal))
        {
            return (null, $"{DevOverrideVariable} must point at this machine or a private network address.");
        }

        // Connect to the checked address so the name can't resolve elsewhere later.
        var pinned = new UriBuilder(uri) { Host = addresses[0].ToString() }.Uri;
        return (pinned, null);
    }

    public static bool IsLocal(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (IPAddress.IsLoopback(address))
        {
            return true;
        }

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = address.GetAddressBytes();
            return b[0] == 10
                || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
                || (b[0] == 192 && b[1] == 168)
                || (b[0] == 169 && b[1] == 254);
        }

        return address.AddressFamily == AddressFamily.InterNetworkV6
            && (address.IsIPv6LinkLocal || address.IsIPv6UniqueLocal);
    }
}
