using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.TrackMemory.Domain;
using Jellyfin.Plugin.TrackMemory.Services;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Session;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.TrackMemory.Tests.Services;

public class PlaybackTrackerServiceTests
{
    private readonly Mock<ISessionManager> _sessionManagerMock;
    private readonly Mock<ITrackMemoryStore> _storeMock;

    public PlaybackTrackerServiceTests()
    {
        _sessionManagerMock = new Mock<ISessionManager>();
        _storeMock = new Mock<ITrackMemoryStore>();
    }

    [Fact]
    public async Task StartAsync_HooksEvents_And_PlaybackProgress_RecordsItemAndSeriesLanguages()
    {
        // Nominal: PlaybackTrackerService started as IHostedService records streams with languages and seriesId
        var service = new PlaybackTrackerService(
            _sessionManagerMock.Object,
            _storeMock.Object,
            NullLogger<PlaybackTrackerService>.Instance);

        await service.StartAsync(CancellationToken.None);

        var userId = Guid.NewGuid();
        var seriesId = Guid.NewGuid();
        var mediaStreams = new List<MediaStream>
        {
            new MediaStream { Type = MediaStreamType.Video, Index = 0 },
            new MediaStream { Type = MediaStreamType.Audio, Index = 1, Language = "fre" },
            new MediaStream { Type = MediaStreamType.Audio, Index = 2, Language = "eng" },
            new MediaStream { Type = MediaStreamType.Subtitle, Index = 3, Language = "fre" },
            new MediaStream { Type = MediaStreamType.Subtitle, Index = 4, Language = "eng" }
        };

        var episode = new TestEpisode(mediaStreams)
        {
            Id = Guid.NewGuid(),
            SeriesId = seriesId,
            Name = "Currahee"
        };

        var session = new SessionInfo(_sessionManagerMock.Object, NullLogger.Instance)
        {
            UserId = userId,
            PlayState = new PlayerStateInfo
            {
                AudioStreamIndex = 2,
                SubtitleStreamIndex = 4
            }
        };

        var args = new PlaybackProgressEventArgs
        {
            Session = session,
            Item = episode
        };

        _sessionManagerMock.Raise(s => s.PlaybackStart += null, args);

        _storeMock.Verify(s => s.SaveSelection(
            userId,
            episode.Id,
            seriesId,
            2,
            4,
            "eng",
            "eng",
            false,
            false,
            false), Times.Once);
    }

    [Fact]
    public async Task ProcessPlaybackEvent_WithSDHSubtitles_PassesHearingImpairedTrue()
    {
        // Nominal: Subtitle stream with IsHearingImpaired = true passes subtitleIsHearingImpaired: true
        var service = new PlaybackTrackerService(
            _sessionManagerMock.Object,
            _storeMock.Object,
            NullLogger<PlaybackTrackerService>.Instance);

        await service.StartAsync(CancellationToken.None);

        var userId = Guid.NewGuid();
        var seriesId = Guid.NewGuid();
        var episode = new TestEpisode(new List<MediaStream>
        {
            new MediaStream { Type = MediaStreamType.Audio, Index = 1, Language = "eng" },
            new MediaStream { Type = MediaStreamType.Subtitle, Index = 7, Language = "eng", IsHearingImpaired = true, IsForced = false }
        })
        {
            Id = Guid.NewGuid(),
            SeriesId = seriesId,
            Name = "Below S01E04"
        };

        var session = new SessionInfo(_sessionManagerMock.Object, NullLogger.Instance)
        {
            UserId = userId,
            PlayState = new PlayerStateInfo
            {
                AudioStreamIndex = 1,
                SubtitleStreamIndex = 7
            }
        };

        _sessionManagerMock.Raise(s => s.PlaybackStart += null, new PlaybackProgressEventArgs
        {
            Session = session,
            Item = episode
        });

        _storeMock.Verify(s => s.SaveSelection(
            userId,
            episode.Id,
            seriesId,
            1,
            7,
            "eng",
            "eng",
            false,
            true,
            false), Times.Once);
    }

    [Fact]
    public async Task ProcessPlaybackEvent_WithSubtitlesDisabled_MarksSubtitleDisabledTrue()
    {
        // Boundary: Subtitle index -1 correctly records subtitleDisabled = true
        var service = new PlaybackTrackerService(
            _sessionManagerMock.Object,
            _storeMock.Object,
            NullLogger<PlaybackTrackerService>.Instance);

        await service.StartAsync(CancellationToken.None);

        var userId = Guid.NewGuid();
        var episode = new TestEpisode(new List<MediaStream>
        {
            new MediaStream { Type = MediaStreamType.Audio, Index = 1, Language = "fre" }
        })
        {
            Id = Guid.NewGuid(),
            Name = "Test Ep"
        };

        var session = new SessionInfo(_sessionManagerMock.Object, NullLogger.Instance)
        {
            UserId = userId,
            PlayState = new PlayerStateInfo
            {
                AudioStreamIndex = 1,
                SubtitleStreamIndex = -1
            }
        };

        _sessionManagerMock.Raise(s => s.PlaybackStart += null, new PlaybackProgressEventArgs
        {
            Session = session,
            Item = episode
        });

        _storeMock.Verify(s => s.SaveSelection(
            userId,
            episode.Id,
            null,
            1,
            -1,
            "fre",
            null,
            true,
            null,
            null), Times.Once);
    }

    [Fact]
    public async Task StopAsync_DetachesEvents_SoSubsequentEventsAreIgnored()
    {
        // Boundary / Lifecycle: StopAsync unhooks events
        var service = new PlaybackTrackerService(
            _sessionManagerMock.Object,
            _storeMock.Object,
            NullLogger<PlaybackTrackerService>.Instance);

        await service.StartAsync(CancellationToken.None);
        await service.StopAsync(CancellationToken.None);

        var session = new SessionInfo(_sessionManagerMock.Object, NullLogger.Instance)
        {
            UserId = Guid.NewGuid(),
            PlayState = new PlayerStateInfo { AudioStreamIndex = 1 }
        };

        _sessionManagerMock.Raise(s => s.PlaybackStart += null, new PlaybackProgressEventArgs
        {
            Session = session,
            Item = new TestEpisode(new List<MediaStream>()) { Id = Guid.NewGuid() }
        });

        _storeMock.Verify(s => s.SaveSelection(
            It.IsAny<Guid>(),
            It.IsAny<Guid>(),
            It.IsAny<Guid?>(),
            It.IsAny<int?>(),
            It.IsAny<int?>(),
            It.IsAny<string?>(),
            It.IsAny<string?>(),
            It.IsAny<bool>(),
            It.IsAny<bool?>(),
            It.IsAny<bool?>()), Times.Never);
    }

    private sealed class TestEpisode : Episode
    {
        private readonly List<MediaStream> _streams;

        public TestEpisode(List<MediaStream> streams)
        {
            _streams = streams;
        }

        public override List<MediaStream> GetMediaStreams() => _streams;
    }
}
