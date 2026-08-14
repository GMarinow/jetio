using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.Jetio;

/// <summary>
/// Jellyfin auto-discovers <see cref="JetioMediaSourceProvider"/> because it implements a known
/// interface, but its dependencies still need registering.
/// </summary>
public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddSingleton<JetioClient>();
    }
}
