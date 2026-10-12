using System;

namespace Jellyfin.Plugin.TrackMemory.Domain;

/// <summary>
/// Represents a user's remembered audio and subtitle stream selection for a specific media item.
/// </summary>
public class TrackSelection
{
    /// <summary>
    /// Gets or sets the Jellyfin media item identifier.
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
    public TrackSelection(Guid itemId, int? audioStreamIndex, int? subtitleStreamIndex)
    {
        ItemId = itemId;
        AudioStreamIndex = audioStreamIndex;
        SubtitleStreamIndex = subtitleStreamIndex;
        UpdatedAtUtc = DateTime.UtcNow;
    }
}
