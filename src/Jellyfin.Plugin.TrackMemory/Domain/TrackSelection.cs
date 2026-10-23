using System;

namespace Jellyfin.Plugin.TrackMemory.Domain;

/// <summary>
/// Represents a user's remembered audio and subtitle stream selection for a specific media item or series.
/// </summary>
public class TrackSelection
{
    /// <summary>
    /// Gets or sets the Jellyfin media item identifier (episode, movie, or series).
    /// </summary>
    public Guid ItemId { get; set; }

    /// <summary>
    /// Gets or sets the preferred audio stream index, or null if none remembered.
    /// </summary>
    public int? AudioStreamIndex { get; set; }

    /// <summary>
    /// Gets or sets the preferred subtitle stream index (-1 or null represents subtitles disabled).
    /// </summary>
    public int? SubtitleStreamIndex { get; set; }

    /// <summary>
    /// Gets or sets the preferred audio stream language code (e.g. "eng", "fre"), if known.
    /// </summary>
    public string? AudioLanguage { get; set; }

    /// <summary>
    /// Gets or sets the preferred subtitle stream language code (e.g. "eng", "fre"), if known.
    /// </summary>
    public string? SubtitleLanguage { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether subtitles are explicitly disabled.
    /// </summary>
    public bool SubtitleDisabled { get; set; }

    /// <summary>
    /// Gets or sets the UTC timestamp when this selection was last updated.
    /// </summary>
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Initializes a new instance of the <see cref="TrackSelection"/> class.
    /// </summary>
    public TrackSelection()
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="TrackSelection"/> class with specified parameters.
    /// </summary>
    /// <param name="itemId">The media item id.</param>
    /// <param name="audioStreamIndex">The audio stream index.</param>
    /// <param name="subtitleStreamIndex">The subtitle stream index.</param>
    /// <param name="audioLanguage">The audio stream language code.</param>
    /// <param name="subtitleLanguage">The subtitle stream language code.</param>
    /// <param name="subtitleDisabled">Whether subtitles are explicitly disabled.</param>
    public TrackSelection(
        Guid itemId,
        int? audioStreamIndex,
        int? subtitleStreamIndex,
        string? audioLanguage = null,
        string? subtitleLanguage = null,
        bool subtitleDisabled = false)
    {
        ItemId = itemId;
        AudioStreamIndex = audioStreamIndex;
        SubtitleStreamIndex = subtitleStreamIndex;
        AudioLanguage = audioLanguage;
        SubtitleLanguage = subtitleLanguage;
        SubtitleDisabled = subtitleDisabled || subtitleStreamIndex == -1;
        UpdatedAtUtc = DateTime.UtcNow;
    }
}
