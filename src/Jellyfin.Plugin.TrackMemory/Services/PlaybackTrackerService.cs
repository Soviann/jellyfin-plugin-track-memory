using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Events;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.TrackMemory.Services;

/// <summary>
/// Service that listens to playback events from ISessionManager to record in-playback audio and subtitle track switches.
/// </summary>
public class PlaybackTrackerService : IHostedService, IDisposable
{
    private readonly ISessionManager _sessionManager;
    private readonly ITrackMemoryStore _store;
    private readonly ILogger<PlaybackTrackerService> _logger;
    private bool _disposed;
    private bool _hooked;

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
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_hooked)
        {
            _sessionManager.PlaybackStart += OnPlaybackStart;
            _sessionManager.PlaybackProgress += OnPlaybackProgress;
            _sessionManager.PlaybackStopped += OnPlaybackStopped;
            _hooked = true;
            _logger.LogInformation("PlaybackTrackerService started and hooked into session playback events.");
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        if (_hooked)
        {
            _sessionManager.PlaybackStart -= OnPlaybackStart;
            _sessionManager.PlaybackProgress -= OnPlaybackProgress;
            _sessionManager.PlaybackStopped -= OnPlaybackStopped;
            _hooked = false;
            _logger.LogInformation("PlaybackTrackerService stopped.");
        }

        return Task.CompletedTask;
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
            Guid? seriesId = null;
            if (item is Episode episode && episode.SeriesId != Guid.Empty)
            {
                seriesId = episode.SeriesId;
            }

            string? audioLang = null;
            string? subLang = null;
            bool? isHearingImpaired = null;
            bool? isForced = null;
            var streams = item.GetMediaStreams();
            if (streams != null)
            {
                if (audioIndex.HasValue)
                {
                    audioLang = streams.FirstOrDefault(s => s.Type == MediaStreamType.Audio && s.Index == audioIndex.Value)?.Language;
                }

                if (subtitleIndex.HasValue && subtitleIndex.Value >= 0)
                {
                    var subStream = streams.FirstOrDefault(s => s.Type == MediaStreamType.Subtitle && s.Index == subtitleIndex.Value);
                    subLang = subStream?.Language;
                    isHearingImpaired = subStream?.IsHearingImpaired;
                    isForced = subStream?.IsForced;
                }
            }

            bool subtitleDisabled = subtitleIndex == -1;

            _logger.LogDebug(
                "[{Source}] User {UserId} playing item {ItemId} ('{ItemName}', series {SeriesId}) with Audio={Audio} ({AudioLang}), Sub={Sub} ({SubLang}), SubDisabled={SubDisabled}, SDH={SDH}, Forced={Forced}",
                sourceEvent,
                userId,
                item.Id,
                item.Name,
                seriesId,
                audioIndex,
                audioLang,
                subtitleIndex,
                subLang,
                subtitleDisabled,
                isHearingImpaired,
                isForced);

            _store.SaveSelection(
                userId,
                item.Id,
                seriesId,
                audioIndex,
                subtitleIndex,
                audioLang,
                subLang,
                subtitleDisabled,
                isHearingImpaired,
                isForced);
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
            Guid? seriesId = null;
            if (item is Episode episode && episode.SeriesId != Guid.Empty)
            {
                seriesId = episode.SeriesId;
            }

            string? audioLang = null;
            string? subLang = null;
            bool? isHearingImpaired = null;
            bool? isForced = null;
            var streams = item.GetMediaStreams();
            if (streams != null)
            {
                if (audioIndex.HasValue)
                {
                    audioLang = streams.FirstOrDefault(s => s.Type == MediaStreamType.Audio && s.Index == audioIndex.Value)?.Language;
                }

                if (subtitleIndex.HasValue && subtitleIndex.Value >= 0)
                {
                    var subStream = streams.FirstOrDefault(s => s.Type == MediaStreamType.Subtitle && s.Index == subtitleIndex.Value);
                    subLang = subStream?.Language;
                    isHearingImpaired = subStream?.IsHearingImpaired;
                    isForced = subStream?.IsForced;
                }
            }

            bool subtitleDisabled = subtitleIndex == -1;

            _logger.LogDebug(
                "[PlaybackStopped] Final recording for user {UserId}, item {ItemId} ('{ItemName}', series {SeriesId}): Audio={Audio} ({AudioLang}), Sub={Sub} ({SubLang}), SubDisabled={SubDisabled}, SDH={SDH}, Forced={Forced}",
                userId,
                item.Id,
                item.Name,
                seriesId,
                audioIndex,
                audioLang,
                subtitleIndex,
                subLang,
                subtitleDisabled,
                isHearingImpaired,
                isForced);

            _store.SaveSelection(
                userId,
                item.Id,
                seriesId,
                audioIndex,
                subtitleIndex,
                audioLang,
                subLang,
                subtitleDisabled,
                isHearingImpaired,
                isForced);
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        if (_hooked)
        {
            _sessionManager.PlaybackStart -= OnPlaybackStart;
            _sessionManager.PlaybackProgress -= OnPlaybackProgress;
            _sessionManager.PlaybackStopped -= OnPlaybackStopped;
            _hooked = false;
        }

        _disposed = true;
        GC.SuppressFinalize(this);
    }
}
