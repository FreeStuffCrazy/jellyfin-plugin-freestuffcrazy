using Jellyfin.Plugin.FreeStuffCrazy.Relay;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.FreeStuffCrazy;

/// <summary>Registers the relay connection, which Jellyfin starts with the server.</summary>
public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddSingleton<ConnectorState>();
        serviceCollection.AddHostedService<RelayClient>();
    }
}
