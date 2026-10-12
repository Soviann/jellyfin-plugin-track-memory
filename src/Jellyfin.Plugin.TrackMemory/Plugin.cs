using System;
using System.Collections.Generic;
using Jellyfin.Plugin.TrackMemory.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.TrackMemory;

/// <summary>
/// The main plugin class for Track Memory.
/// </summary>
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    /// <summary>
    /// Initializes a new instance of the <see cref="Plugin"/> class.
    /// </summary>
    /// <param name="applicationPaths">The application paths.</param>
    /// <param name="xmlSerializer">The XML serializer.</param>
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    /// <summary>
    /// Gets the current plugin instance.
    /// </summary>
    public static Plugin? Instance { get; private set; }

    /// <inheritdoc />
    public override string Name => "Track Memory";

    /// <inheritdoc />
    public override Guid Id => Guid.Parse("5180f998-b8fc-41e0-8c6e-005f4564ec7a");

    /// <inheritdoc />
    public override string Description => "Remembers chosen audio and subtitle tracks per title across sessions and rewatches.";

    /// <inheritdoc />
    public IEnumerable<PluginPageInfo> GetPages()
    {
        return new[]
        {
            new PluginPageInfo
            {
                Name = this.Name,
                EmbeddedResourcePath = string.Format(
                    System.Globalization.CultureInfo.InvariantCulture,
                    "{0}.Configuration.Web.config.html",
                    GetType().Namespace)
            },
            new PluginPageInfo
            {
                Name = "TrackMemory.js",
                EmbeddedResourcePath = string.Format(
                    System.Globalization.CultureInfo.InvariantCulture,
                    "{0}.Configuration.Web.config.js",
                    GetType().Namespace)
            }
        };
    }
}
