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
}
