using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.TrackMemory.Configuration;

/// <summary>
/// Configuration options for the Track Memory plugin.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>
    /// Gets or sets a value indicating whether the plugin is enabled.
    /// </summary>
    public bool EnablePlugin { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether audio track selections should be remembered and restored.
    /// </summary>
    public bool EnableAudioMemory { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether subtitle track selections should be remembered and restored.
    /// </summary>
    public bool EnableSubtitleMemory { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether debug logging is enabled.
    /// </summary>
    public bool EnableDebugLogging { get; set; } = false;
}
