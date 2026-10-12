using System;
using MediaBrowser.Controller.Events;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.TrackMemory.Services;

/// <summary>
/// Service that listens to playback events from ISessionManager to record in-playback audio and subtitle track switches.
/// </summary>
public class PlaybackTrackerService : IDisposable
{
    private readonly ISessionManager _sessionManager;
    private readonly ITrackMemoryStore _store;
    private readonly ILogger<PlaybackTrackerService> _logger;
    private bool _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="PlaybackTrackerService"/> class.
    /// </summary>
    /// <param name="sessionManager">The Jellyfin session manager.</param>
    /// <param name="store">The track memory store.</param>
    /// <param name="logger">The logger instance.</param>
    public PlaybackTrackerService(
        ISessionManager sessionManager,
        ITrackMemoryStore store,
        ILogger<PlaybackTrackerService> logger)
    {
        _sessionManager = sessionManager;
        _store = store;
        _logger = logger;

        _sessionManager.PlaybackStart += OnPlaybackStart;
        _sessionManager.PlaybackProgress += OnPlaybackProgress;
        _sessionManager.PlaybackStopped += OnPlaybackStopped;
    }

    private void OnPlaybackStart(object? sender, PlaybackProgressEventArgs e)
    {
        ProcessPlaybackEvent(e, "PlaybackStart");
    }

    private void OnPlaybackProgress(object? sender, PlaybackProgressEventArgs e)
    {
        ProcessPlaybackEvent(e, "PlaybackProgress");
    }

    private void OnPlaybackStopped(object? sender, PlaybackStopEventArgs e)
    {
        ProcessPlaybackStopEvent(e);
    }

    private void ProcessPlaybackEvent(PlaybackProgressEventArgs e, string sourceEvent)
    {
        var config = Plugin.Instance?.Configuration;
        if (config != null && !config.EnablePlugin)
        {
            return;
        }

        var session = e.Session;
        if (session == null)
        {
            return;
        }

        var userId = session.UserId;
        var item = e.Item;
        if (userId == Guid.Empty || item == null)
        {
            return;
        }

        var playState = session.PlayState;
        if (playState == null)
        {
            return;
        }

        int? audioIndex = playState.AudioStreamIndex;
        int? subtitleIndex = playState.SubtitleStreamIndex;

        // If plugin configuration disables audio or subtitle memory specifically:
        if (config != null)
        {
            if (!config.EnableAudioMemory)
            {
                audioIndex = null;
            }
            if (!config.EnableSubtitleMemory)
            {
                subtitleIndex = null;
            }
        }

        // Only save if at least one stream is captured
        if (audioIndex.HasValue || subtitleIndex.HasValue)
        {
            _logger.LogDebug(
                "[{Source}] User {UserId} playing item {ItemId} ('{ItemName}') with Audio={Audio}, Sub={Sub}",
                sourceEvent,
                userId,
                item.Id,
                item.Name,
                audioIndex,
                subtitleIndex);

            _store.SaveSelection(userId, item.Id, audioIndex, subtitleIndex);
        }
    }

    private void ProcessPlaybackStopEvent(PlaybackStopEventArgs e)
    {
        var config = Plugin.Instance?.Configuration;
        if (config != null && !config.EnablePlugin)
        {
            return;
        }

        var session = e.Session;
        if (session == null)
        {
            return;
        }

        var userId = session.UserId;
        var item = e.Item;
        if (userId == Guid.Empty || item == null)
        {
            return;
        }

        var playState = session.PlayState;
        if (playState == null)
        {
            return;
        }

        int? audioIndex = playState.AudioStreamIndex;
        int? subtitleIndex = playState.SubtitleStreamIndex;

        if (config != null)
        {
            if (!config.EnableAudioMemory)
            {
                audioIndex = null;
            }
            if (!config.EnableSubtitleMemory)
            {
                subtitleIndex = null;
            }
        }

        if (audioIndex.HasValue || subtitleIndex.HasValue)
        {
            _logger.LogDebug(
                "[PlaybackStopped] Final recording for user {UserId}, item {ItemId} ('{ItemName}'): Audio={Audio}, Sub={Sub}",
                userId,
                item.Id,
                item.Name,
                audioIndex,
                subtitleIndex);

            _store.SaveSelection(userId, item.Id, audioIndex, subtitleIndex);
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _sessionManager.PlaybackStart -= OnPlaybackStart;
        _sessionManager.PlaybackProgress -= OnPlaybackProgress;
        _sessionManager.PlaybackStopped -= OnPlaybackStopped;
        _disposed = true;
        GC.SuppressFinalize(this);
    }
}
