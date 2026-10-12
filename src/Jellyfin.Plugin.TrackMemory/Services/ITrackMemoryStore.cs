using System;
using System.Threading.Tasks;
using Jellyfin.Plugin.TrackMemory.Domain;

namespace Jellyfin.Plugin.TrackMemory.Services;

/// <summary>
/// Interface for thread-safe track memory storage and retrieval.
/// </summary>
public interface ITrackMemoryStore
{
    /// <summary>
    /// Gets the remembered track selection for the specified user and item.
    /// </summary>
    /// <param name="userId">The user identifier.</param>
    /// <param name="itemId">The media item identifier.</param>
    /// <returns>The remembered track selection, or null if not found.</returns>
    TrackSelection? GetSelection(Guid userId, Guid itemId);

    /// <summary>
    /// Saves or updates the remembered track selection for the specified user and item.
    /// </summary>
    /// <param name="userId">The user identifier.</param>
    /// <param name="itemId">The media item identifier.</param>
    /// <param name="audioStreamIndex">The audio stream index.</param>
    /// <param name="subtitleStreamIndex">The subtitle stream index.</param>
    void SaveSelection(Guid userId, Guid itemId, int? audioStreamIndex, int? subtitleStreamIndex);

    /// <summary>
    /// Flushes all in-memory changes to the underlying storage file.
    /// </summary>
    /// <returns>A task representing the flush operation.</returns>
    Task FlushAsync();

    /// <summary>
    /// Clears all stored selections (useful for maintenance or testing).
    /// </summary>
    void Clear();
}
