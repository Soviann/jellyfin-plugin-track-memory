using System.IO;
using Jellyfin.Plugin.TrackMemory.Api;
using Jellyfin.Plugin.TrackMemory.Services;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.TrackMemory;

/// <summary>
/// Registers plugin services, tracker and middleware into the Jellyfin dependency injection container.
/// </summary>
public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    /// <inheritdoc />
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        // Register single ITrackMemoryStore backed by the plugin's configuration data directory
        serviceCollection.AddSingleton<ITrackMemoryStore>(sp =>
        {
            var appPaths = sp.GetRequiredService<IApplicationPaths>();
            var logger = sp.GetRequiredService<ILogger<TrackMemoryStore>>();

            var pluginDataDir = Path.Combine(appPaths.PluginConfigurationsPath, "Jellyfin.Plugin.TrackMemory");
            return new TrackMemoryStore(pluginDataDir, logger);
        });

        // Register the real-time playback tracker service (instantiated as singleton)
        serviceCollection.AddSingleton<PlaybackTrackerService>();

        // Register the startup filter to insert TrackMemoryMiddleware into ASP.NET Core pipeline
        serviceCollection.AddTransient<IStartupFilter, StartupFilter>();
    }
}
