using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.BiliArchive;

public sealed class PluginServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddSingleton<ArchiveStore>();
        serviceCollection.AddSingleton<SyncStateStore>();
        serviceCollection.AddSingleton<BiliApi>();
        serviceCollection.AddSingleton<ArchiveService>();
    }
}
