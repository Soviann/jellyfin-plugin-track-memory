using System;
using System.IO;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Jellyfin.Plugin.TrackMemory.Api;
using Jellyfin.Plugin.TrackMemory.Configuration;
using Jellyfin.Plugin.TrackMemory.Domain;
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
}
