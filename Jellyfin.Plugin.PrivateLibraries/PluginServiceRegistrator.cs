using Jellyfin.Plugin.PrivateLibraries.Services;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.PrivateLibraries;

/// <summary>
/// Registers the plugin's services with Jellyfin's dependency injection container.
/// </summary>
public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    /// <inheritdoc />
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddSingleton<RestrictionManager>();
        serviceCollection.AddHostedService<ItemAddedListener>();
        serviceCollection.AddHostedService<ScriptInjector>();
    }
}
