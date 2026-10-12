using System;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;

namespace Jellyfin.Plugin.TrackMemory.Api;

/// <summary>
/// Registers the TrackMemoryMiddleware into the ASP.NET Core request pipeline.
/// </summary>
public class StartupFilter : IStartupFilter
{
    /// <inheritdoc />
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
    {
        return app =>
        {
            app.UseMiddleware<TrackMemoryMiddleware>();
            next(app);
        };
    }
}
