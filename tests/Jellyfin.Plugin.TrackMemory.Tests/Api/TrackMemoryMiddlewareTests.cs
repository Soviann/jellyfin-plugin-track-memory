using System;
using System.IO;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Jellyfin.Plugin.TrackMemory.Api;
using Jellyfin.Plugin.TrackMemory.Configuration;
using Jellyfin.Plugin.TrackMemory.Domain;
using Jellyfin.Plugin.TrackMemory.Services;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.TrackMemory.Tests.Api;

public class TrackMemoryMiddlewareTests
{
    private const string SamplePlaybackInfoJson = @"{
        ""MediaSources"": [
            {
                ""Id"": ""source-1"",
                ""DefaultAudioStreamIndex"": 1,
                ""DefaultSubtitleStreamIndex"": -1,
                ""MediaStreams"": [
                    { ""Type"": ""Video"", ""Index"": 0 },
                    { ""Type"": ""Audio"", ""Index"": 1, ""Language"": ""eng"" },
                    { ""Type"": ""Audio"", ""Index"": 2, ""Language"": ""fre"" },
                    { ""Type"": ""Subtitle"", ""Index"": 3, ""Language"": ""fre"" }
                ]
            }
        ]
    }";

    [Fact]
    public async Task RewritePlaybackInfoResponseAsync_NoRememberedSelection_LeavesPayloadUnchanged()
    {
        // Nominal: When no selection is remembered, payload is not altered
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(SamplePlaybackInfoJson));
        var config = new PluginConfiguration();

        var resultBytes = await TrackMemoryMiddleware.RewritePlaybackInfoResponseAsync(
            stream,
            remembered: null,
            explicitRequest: (null, null),
            config: config);

        var json = Encoding.UTF8.GetString(resultBytes);
        var node = JsonNode.Parse(json);
        Assert.NotNull(node);

        var defaultAudio = node["MediaSources"]?[0]?["DefaultAudioStreamIndex"]?.GetValue<int>();
        var defaultSub = node["MediaSources"]?[0]?["DefaultSubtitleStreamIndex"]?.GetValue<int>();

        Assert.Equal(1, defaultAudio);
        Assert.Equal(-1, defaultSub);
    }

    [Fact]
    public async Task RewritePlaybackInfoResponseAsync_WithRememberedSelection_RewritesDefaults()
    {
        // Nominal: When remembered selection exists and request had no override, defaults are updated
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(SamplePlaybackInfoJson));
        var config = new PluginConfiguration();
        var remembered = new TrackSelection(Guid.NewGuid(), audioStreamIndex: 2, subtitleStreamIndex: 3);

        var resultBytes = await TrackMemoryMiddleware.RewritePlaybackInfoResponseAsync(
            stream,
            remembered: remembered,
            explicitRequest: (null, null),
            config: config);

        var json = Encoding.UTF8.GetString(resultBytes);
        var node = JsonNode.Parse(json);
        Assert.NotNull(node);

        var defaultAudio = node["MediaSources"]?[0]?["DefaultAudioStreamIndex"]?.GetValue<int>();
        var defaultSub = node["MediaSources"]?[0]?["DefaultSubtitleStreamIndex"]?.GetValue<int>();

        Assert.Equal(2, defaultAudio);
        Assert.Equal(3, defaultSub);
    }

    [Fact]
    public async Task RewritePlaybackInfoResponseAsync_WithExplicitRequestOverride_DoesNotOverwriteExplicitSelection()
    {
        // Boundary: When user explicitly picked audio on details page (explicitRequest.AudioIndex = 1),
        // remembered audio (2) is NOT forced over the user's immediate choice.
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(SamplePlaybackInfoJson));
        var config = new PluginConfiguration();
        var remembered = new TrackSelection(Guid.NewGuid(), audioStreamIndex: 2, subtitleStreamIndex: 3);

        var resultBytes = await TrackMemoryMiddleware.RewritePlaybackInfoResponseAsync(
            stream,
            remembered: remembered,
            explicitRequest: (AudioIndex: 1, SubtitleIndex: null),
            config: config);

        var json = Encoding.UTF8.GetString(resultBytes);
        var node = JsonNode.Parse(json);
        Assert.NotNull(node);

        var defaultAudio = node["MediaSources"]?[0]?["DefaultAudioStreamIndex"]?.GetValue<int>();
        var defaultSub = node["MediaSources"]?[0]?["DefaultSubtitleStreamIndex"]?.GetValue<int>();

        // Audio was explicitly set to 1 in request -> not rewritten to 2
        Assert.Equal(1, defaultAudio);
        // Subtitle was not explicit -> rewritten to remembered 3
        Assert.Equal(3, defaultSub);
    }

    [Fact]
    public async Task RewritePlaybackInfoResponseAsync_PluginDisabled_LeavesPayloadUnchanged()
    {
        // Boundary: Plugin disabled in configuration
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(SamplePlaybackInfoJson));
        var config = new PluginConfiguration { EnablePlugin = false };
        var remembered = new TrackSelection(Guid.NewGuid(), audioStreamIndex: 2, subtitleStreamIndex: 3);

        var resultBytes = await TrackMemoryMiddleware.RewritePlaybackInfoResponseAsync(
            stream,
            remembered: remembered,
            explicitRequest: (null, null),
            config: config);

        var json = Encoding.UTF8.GetString(resultBytes);
        var node = JsonNode.Parse(json);
        Assert.NotNull(node);

        var defaultAudio = node["MediaSources"]?[0]?["DefaultAudioStreamIndex"]?.GetValue<int>();
        Assert.Equal(1, defaultAudio);
    }

    [Fact]
    public async Task RewritePlaybackInfoResponseAsync_InvalidJson_ReturnsOriginalBytesSafely()
    {
        // Error: Malformed JSON stream does not throw exception
        var invalidBytes = Encoding.UTF8.GetBytes("INVALID NOT JSON { [");
        using var stream = new MemoryStream(invalidBytes);
        var config = new PluginConfiguration();
        var remembered = new TrackSelection(Guid.NewGuid(), audioStreamIndex: 2, subtitleStreamIndex: 3);

        var resultBytes = await TrackMemoryMiddleware.RewritePlaybackInfoResponseAsync(
            stream,
            remembered: remembered,
            explicitRequest: (null, null),
            config: config);

        Assert.Equal(invalidBytes, resultBytes);
    }

    [Fact]
    public async Task RewritePlaybackInfoResponseAsync_WithLanguages_MatchesStreamsByLanguageCode()
    {
        // Nominal: When remembered selection has language codes, matching stream index is resolved
        // In SamplePlaybackInfoJson: stream 2 is fre audio, stream 3 is fre sub
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(SamplePlaybackInfoJson));
        var config = new PluginConfiguration();
        var remembered = new TrackSelection(Guid.NewGuid(), audioStreamIndex: null, subtitleStreamIndex: null, audioLanguage: "fre", subtitleLanguage: "fre");

        var resultBytes = await TrackMemoryMiddleware.RewritePlaybackInfoResponseAsync(
            stream,
            remembered: remembered,
            explicitRequest: (null, null),
            config: config);

        var json = Encoding.UTF8.GetString(resultBytes);
        var node = JsonNode.Parse(json);
        Assert.NotNull(node);

        var defaultAudio = node["MediaSources"]?[0]?["DefaultAudioStreamIndex"]?.GetValue<int>();
        var defaultSub = node["MediaSources"]?[0]?["DefaultSubtitleStreamIndex"]?.GetValue<int>();

        Assert.Equal(2, defaultAudio);
        Assert.Equal(3, defaultSub);
    }

    [Fact]
    public async Task RewritePlaybackInfoResponseAsync_WithSubtitleDisabled_SetsDefaultSubtitleStreamIndexToMinusOne()
    {
        // Boundary: SubtitleDisabled flag forces DefaultSubtitleStreamIndex to -1
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(SamplePlaybackInfoJson));
        var config = new PluginConfiguration();
        var remembered = new TrackSelection(Guid.NewGuid(), audioStreamIndex: 1, subtitleStreamIndex: 3, subtitleDisabled: true);

        var resultBytes = await TrackMemoryMiddleware.RewritePlaybackInfoResponseAsync(
            stream,
            remembered: remembered,
            explicitRequest: (null, null),
            config: config);

        var json = Encoding.UTF8.GetString(resultBytes);
        var node = JsonNode.Parse(json);
        Assert.NotNull(node);

        var defaultSub = node["MediaSources"]?[0]?["DefaultSubtitleStreamIndex"]?.GetValue<int>();
        Assert.Equal(-1, defaultSub);
    }

    [Fact]
    public void ParseStreamIndicesFromJson_HandlesBothNumbersAndStrings()
    {
        // Error / Boundary regression test for InvalidOperationException:
        // Client sending strings {"AudioStreamIndex": "2", "SubtitleStreamIndex": "3"}
        var jsonWithStrings = @"{ ""AudioStreamIndex"": ""2"", ""SubtitleStreamIndex"": ""3"" }";
        var (audio1, sub1) = TrackMemoryMiddleware.ParseJsonStreamIndices(jsonWithStrings);
        Assert.Equal(2, audio1);
        Assert.Equal(3, sub1);

        // Standard integer JSON
        var jsonWithNumbers = @"{ ""AudioStreamIndex"": 1, ""SubtitleStreamIndex"": -1 }";
        var (audio2, sub2) = TrackMemoryMiddleware.ParseJsonStreamIndices(jsonWithNumbers);
        Assert.Equal(1, audio2);
        Assert.Equal(-1, sub2);
    }

    private const string SamplePlaybackInfoWithVariantsJson = @"{
        ""MediaSources"": [
            {
                ""Id"": ""source-below"",
                ""DefaultAudioStreamIndex"": 2,
                ""DefaultSubtitleStreamIndex"": 3,
                ""MediaStreams"": [
                    { ""Type"": ""Video"", ""Index"": 0 },
                    { ""Type"": ""Audio"", ""Index"": 1, ""Language"": ""eng"" },
                    { ""Type"": ""Audio"", ""Index"": 2, ""Language"": ""fre"" },
                    { ""Type"": ""Subtitle"", ""Index"": 3, ""Language"": ""fre"", ""IsForced"": true },
                    { ""Type"": ""Subtitle"", ""Index"": 4, ""Language"": ""fre"", ""IsForced"": false, ""IsHearingImpaired"": false },
                    { ""Type"": ""Subtitle"", ""Index"": 5, ""Language"": ""eng"", ""IsForced"": true, ""Title"": ""English (Forced)"" },
                    { ""Type"": ""Subtitle"", ""Index"": 6, ""Language"": ""eng"", ""IsForced"": false, ""IsHearingImpaired"": false, ""Title"": ""English (Full)"" },
                    { ""Type"": ""Subtitle"", ""Index"": 7, ""Language"": ""eng"", ""IsForced"": false, ""IsHearingImpaired"": true, ""Title"": ""English (SDH)"" }
                ]
            }
        ]
    }";

    [Fact]
    public async Task RewritePlaybackInfoResponseAsync_WithSDHPreference_ResolvesSDHStreamIndex()
    {
        // Nominal: SDH / Hearing impaired preference specifically resolves stream 7
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(SamplePlaybackInfoWithVariantsJson));
        var config = new PluginConfiguration();
        var remembered = new TrackSelection(
            Guid.NewGuid(),
            audioStreamIndex: null,
            subtitleStreamIndex: null,
            audioLanguage: "eng",
            subtitleLanguage: "eng",
            subtitleDisabled: false,
            subtitleIsHearingImpaired: true,
            subtitleIsForced: false);

        var resultBytes = await TrackMemoryMiddleware.RewritePlaybackInfoResponseAsync(
            stream,
            remembered: remembered,
            explicitRequest: (null, null),
            config: config);

        var json = Encoding.UTF8.GetString(resultBytes);
        var node = JsonNode.Parse(json);
        Assert.NotNull(node);

        var defaultAudio = node["MediaSources"]?[0]?["DefaultAudioStreamIndex"]?.GetValue<int>();
        var defaultSub = node["MediaSources"]?[0]?["DefaultSubtitleStreamIndex"]?.GetValue<int>();

        Assert.Equal(1, defaultAudio);
        Assert.Equal(7, defaultSub); // Stream 7 is English SDH
    }

    [Fact]
    public async Task RewritePlaybackInfoResponseAsync_WithFullPreference_ResolvesFullStreamIndex()
    {
        // Nominal: Standard full subtitles specifically resolve stream 6
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(SamplePlaybackInfoWithVariantsJson));
        var config = new PluginConfiguration();
        var remembered = new TrackSelection(
            Guid.NewGuid(),
            audioStreamIndex: null,
            subtitleStreamIndex: null,
            audioLanguage: "eng",
            subtitleLanguage: "eng",
            subtitleDisabled: false,
            subtitleIsHearingImpaired: false,
            subtitleIsForced: false);

        var resultBytes = await TrackMemoryMiddleware.RewritePlaybackInfoResponseAsync(
            stream,
            remembered: remembered,
            explicitRequest: (null, null),
            config: config);

        var json = Encoding.UTF8.GetString(resultBytes);
        var node = JsonNode.Parse(json);
        Assert.NotNull(node);

        var defaultSub = node["MediaSources"]?[0]?["DefaultSubtitleStreamIndex"]?.GetValue<int>();
        Assert.Equal(6, defaultSub); // Stream 6 is English Full
    }

    [Fact]
    public async Task RewritePlaybackInfoResponseAsync_WithForcedPreference_ResolvesForcedStreamIndex()
    {
        // Nominal: Forced subtitles specifically resolve stream 5
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(SamplePlaybackInfoWithVariantsJson));
        var config = new PluginConfiguration();
        var remembered = new TrackSelection(
            Guid.NewGuid(),
            audioStreamIndex: null,
            subtitleStreamIndex: null,
            audioLanguage: "eng",
            subtitleLanguage: "eng",
            subtitleDisabled: false,
            subtitleIsHearingImpaired: false,
            subtitleIsForced: true);

        var resultBytes = await TrackMemoryMiddleware.RewritePlaybackInfoResponseAsync(
            stream,
            remembered: remembered,
            explicitRequest: (null, null),
            config: config);

        var json = Encoding.UTF8.GetString(resultBytes);
        var node = JsonNode.Parse(json);
        Assert.NotNull(node);

        var defaultSub = node["MediaSources"]?[0]?["DefaultSubtitleStreamIndex"]?.GetValue<int>();
        Assert.Equal(5, defaultSub); // Stream 5 is English Forced
    }

    [Theory]
    [InlineData("/Users/a18afe1f-34df-4655-8c77-486b160e29b8/Items/b59cf94a-8301-5eea-5215-e3bd52d0a75f", true, false, "a18afe1f-34df-4655-8c77-486b160e29b8", "b59cf94a-8301-5eea-5215-e3bd52d0a75f")]
    [InlineData("/Items/b59cf94a-8301-5eea-5215-e3bd52d0a75f/PlaybackInfo", true, true, null, "b59cf94a-8301-5eea-5215-e3bd52d0a75f")]
    [InlineData("/Items/b59cf94a-8301-5eea-5215-e3bd52d0a75f", true, false, null, "b59cf94a-8301-5eea-5215-e3bd52d0a75f")]
    [InlineData("/Items/b59cf94a-8301-5eea-5215-e3bd52d0a75f/Images/Primary", false, false, null, null)]
    [InlineData("/Shows/NextUp", false, false, null, null)]
    public void TryMatchInterceptedRequest_MatchesRoutesAccurately(
        string path,
        bool expectedMatch,
        bool expectedIsPlaybackInfo,
        string? expectedUserId,
        string? expectedItemId)
    {
        // Boundary: Route matching accurately recognizes details pages and playback info endpoints
        var matched = TrackMemoryMiddleware.TryMatchInterceptedRequest(
            path,
            out var itemId,
            out var routeUserId,
            out var isPlaybackInfo);

        Assert.Equal(expectedMatch, matched);
        if (expectedMatch)
        {
            Assert.Equal(expectedIsPlaybackInfo, isPlaybackInfo);
            if (expectedUserId != null)
            {
                Assert.Equal(Guid.Parse(expectedUserId), routeUserId);
            }
            else
            {
                Assert.Null(routeUserId);
            }

            if (expectedItemId != null)
            {
                Assert.Equal(Guid.Parse(expectedItemId), itemId);
            }
        }
    }

    private const string SampleItemDetailsJson = @"{
        ""Name"": ""Below - S01E05 - Little Pot Rock"",
        ""Id"": ""b59cf94a-8301-5eea-5215-e3bd52d0a75f"",
        ""MediaSources"": [
            {
                ""Id"": ""source-below"",
                ""DefaultAudioStreamIndex"": 2,
                ""DefaultSubtitleStreamIndex"": 3,
                ""MediaStreams"": [
                    { ""Type"": ""Video"", ""Index"": 0 },
                    { ""Type"": ""Audio"", ""Index"": 1, ""Language"": ""eng"" },
                    { ""Type"": ""Audio"", ""Index"": 2, ""Language"": ""fre"" },
                    { ""Type"": ""Subtitle"", ""Index"": 3, ""Language"": ""fre"", ""IsForced"": true },
                    { ""Type"": ""Subtitle"", ""Index"": 5, ""Language"": ""eng"", ""IsHearingImpaired"": false },
                    { ""Type"": ""Subtitle"", ""Index"": 6, ""Language"": ""eng"", ""IsHearingImpaired"": true, ""Title"": ""SDH"" }
                ]
            }
        ]
    }";

    [Fact]
    public async Task RewritePlaybackInfoResponseAsync_ItemDetailsPayload_RewritesDefaultTracks()
    {
        // Nominal: BaseItemDto item details payload from GET /Users/{userId}/Items/{itemId} is rewritten
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(SampleItemDetailsJson));
        var config = new PluginConfiguration();
        var remembered = new TrackSelection(
            Guid.Parse("b59cf94a-8301-5eea-5215-e3bd52d0a75f"),
            audioStreamIndex: null,
            subtitleStreamIndex: null,
            audioLanguage: "eng",
            subtitleLanguage: "eng",
            subtitleDisabled: false,
            subtitleIsHearingImpaired: true,
            subtitleIsForced: false);

        var resultBytes = await TrackMemoryMiddleware.RewritePlaybackInfoResponseAsync(
            stream,
            remembered: remembered,
            explicitRequest: (null, null),
            config: config);

        var json = Encoding.UTF8.GetString(resultBytes);
        var node = JsonNode.Parse(json);
        Assert.NotNull(node);

        var defaultAudio = node["MediaSources"]?[0]?["DefaultAudioStreamIndex"]?.GetValue<int>();
        var defaultSub = node["MediaSources"]?[0]?["DefaultSubtitleStreamIndex"]?.GetValue<int>();

        Assert.Equal(1, defaultAudio);
        Assert.Equal(6, defaultSub); // Stream 6 is English SDH
    }

    [Fact]
    public async Task InvokeAsync_PlaybackInfoRequest_NeverMutatesTrackMemoryStore()
    {
        // Boundary: PlaybackInfo evaluation never writes or mutates TrackMemoryStore
        var storeMock = new Mock<ITrackMemoryStore>();
        var userManagerMock = new Mock<IUserManager>();
        var libraryManagerMock = new Mock<ILibraryManager>();

        var itemId = Guid.NewGuid();
        var userId = Guid.NewGuid();

        storeMock.Setup(s => s.GetSelection(userId, itemId, null))
            .Returns(new TrackSelection(itemId, 1, 6, "eng", "eng", false));

        var middleware = new TrackMemoryMiddleware(
            async ctx =>
            {
                ctx.Response.StatusCode = 200;
                ctx.Response.ContentType = "application/json";
                await ctx.Response.WriteAsync(SamplePlaybackInfoWithVariantsJson);
            },
            NullLogger<TrackMemoryMiddleware>.Instance,
            storeMock.Object,
            userManagerMock.Object,
            libraryManagerMock.Object);

        var context = new DefaultHttpContext();
        context.Request.Path = $"/Items/{itemId}/PlaybackInfo";
        context.Request.QueryString = new QueryString($"?userId={userId}");
        context.Request.Method = "POST";
        context.Request.ContentType = "application/json";

        var requestBody = @"{ ""AudioStreamIndex"": 2, ""SubtitleStreamIndex"": 3 }";
        var bodyBytes = Encoding.UTF8.GetBytes(requestBody);
        context.Request.Body = new MemoryStream(bodyBytes);
        context.Request.ContentLength = bodyBytes.Length;

        context.Response.Body = new MemoryStream();

        await middleware.InvokeAsync(context);

        // Verify that SaveSelection was NEVER called on PlaybackInfo
        storeMock.Verify(s => s.SaveSelection(
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

    [Fact]
    public async Task InvokeAsync_ItemDetailsRequest_RewritesMediaSourcesWithRememberedTracks()
    {
        // Nominal: GET /Users/{userId}/Items/{itemId} rewrites default tracks from remembered selection
        var storeMock = new Mock<ITrackMemoryStore>();
        var userManagerMock = new Mock<IUserManager>();
        var libraryManagerMock = new Mock<ILibraryManager>();

        var itemId = Guid.NewGuid();
        var userId = Guid.NewGuid();

        storeMock.Setup(s => s.GetSelection(userId, itemId, null))
            .Returns(new TrackSelection(itemId, null, null, "eng", "eng", false, true, false));

        var middleware = new TrackMemoryMiddleware(
            async ctx =>
            {
                ctx.Response.StatusCode = 200;
                ctx.Response.ContentType = "application/json";
                await ctx.Response.WriteAsync(SamplePlaybackInfoWithVariantsJson);
            },
            NullLogger<TrackMemoryMiddleware>.Instance,
            storeMock.Object,
            userManagerMock.Object,
            libraryManagerMock.Object);

        var context = new DefaultHttpContext();
        context.Request.Path = $"/Users/{userId}/Items/{itemId}";
        context.Request.Method = "GET";
        context.Response.Body = new MemoryStream();

        await middleware.InvokeAsync(context);

        context.Response.Body.Seek(0, SeekOrigin.Begin);
        using var reader = new StreamReader(context.Response.Body);
        var responseText = await reader.ReadToEndAsync();
        var node = JsonNode.Parse(responseText);
        Assert.NotNull(node);

        var defaultAudio = node["MediaSources"]?[0]?["DefaultAudioStreamIndex"]?.GetValue<int>();
        var defaultSub = node["MediaSources"]?[0]?["DefaultSubtitleStreamIndex"]?.GetValue<int>();

        Assert.Equal(1, defaultAudio); // English VO
        Assert.Equal(7, defaultSub);   // English SDH
    }
}
