using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.FreeStuffCrazy.Configuration;

/// <summary>
/// Saved by Jellyfin in its plugin configuration folder, which only the
/// Jellyfin service account can read. The secret is rotated by the relay on
/// every connection.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>Gets or sets a value indicating whether the server connects to the relay.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Gets or sets the relay's ID for this server, once linked.</summary>
    public string ConnectorId { get; set; } = string.Empty;

    /// <summary>Gets or sets the current credential secret, once linked.</summary>
    public string Secret { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets a development relay address, such as ws://192.168.0.10:3000/connect.
    /// Not shown on the plugin page. Like FREESTUFFCRAZY_DEV_RELAY, which takes
    /// precedence, it is honored only for local or private addresses.
    /// </summary>
    public string DevRelayUrl { get; set; } = string.Empty;
}
