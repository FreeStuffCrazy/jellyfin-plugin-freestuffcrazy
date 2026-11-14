using Jellyfin.Plugin.FreeStuffCrazy.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.FreeStuffCrazy;

/// <summary>
/// Lets viewers watch this server from FreeStuffCrazy Player without opening
/// a port: the server keeps an outbound connection to the player's relay.
/// </summary>
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    public static Plugin? Instance { get; private set; }

    public override string Name => "FreeStuffCrazy Player Connector";

    public override Guid Id => Guid.Parse("f0a67b55-b968-4ba2-9614-2428d2a24dd8");

    public override string Description => "Watch this server from FreeStuffCrazy Player without opening ports.";

    public IEnumerable<PluginPageInfo> GetPages() =>
    [
        new PluginPageInfo
        {
            Name = Name,
            EmbeddedResourcePath = $"{GetType().Namespace}.Configuration.configPage.html",
        },
    ];
}
