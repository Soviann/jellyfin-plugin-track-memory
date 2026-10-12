using System;
using System.IO;
using System.Threading.Tasks;
using Jellyfin.Plugin.TrackMemory.Domain;
using Jellyfin.Plugin.TrackMemory.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.TrackMemory.Tests.Services;

public class TrackMemoryStoreTests : IDisposable
{
    private readonly string _testDir;

    public TrackMemoryStoreTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "TrackMemoryStoreTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_testDir))
        {
            Directory.Delete(_testDir, true);
        }
    }

    [Fact]
    public void GetSelection_NonExistentItem_ReturnsNull()
    {
        // Nominal / Boundary: Asking for an unknown item returns null
        using var store = new TrackMemoryStore(_testDir, NullLogger<TrackMemoryStore>.Instance, autoFlushIntervalMs: 0);

        var result = store.GetSelection(Guid.NewGuid(), Guid.NewGuid());

        Assert.Null(result);
    }

    [Fact]
    public void SaveSelection_ValidSelection_RetrievesImmediatelyFromCache()
    {
        // Nominal: Storing selection is immediately accessible
        using var store = new TrackMemoryStore(_testDir, NullLogger<TrackMemoryStore>.Instance, autoFlushIntervalMs: 0);
        var userId = Guid.NewGuid();
        var itemId = Guid.NewGuid();

        store.SaveSelection(userId, itemId, audioStreamIndex: 1, subtitleStreamIndex: 2);
        var result = store.GetSelection(userId, itemId);

        Assert.NotNull(result);
        Assert.Equal(itemId, result.ItemId);
        Assert.Equal(1, result.AudioStreamIndex);
        Assert.Equal(2, result.SubtitleStreamIndex);
    }

    [Fact]
    public void SaveSelection_WithDisabledSubtitles_StoresNegativeOrNull()
    {
        // Boundary: Subtitles disabled (e.g. index -1)
        using var store = new TrackMemoryStore(_testDir, NullLogger<TrackMemoryStore>.Instance, autoFlushIntervalMs: 0);
        var userId = Guid.NewGuid();
        var itemId = Guid.NewGuid();

        store.SaveSelection(userId, itemId, audioStreamIndex: 2, subtitleStreamIndex: -1);
        var result = store.GetSelection(userId, itemId);

        Assert.NotNull(result);
        Assert.Equal(2, result.AudioStreamIndex);
        Assert.Equal(-1, result.SubtitleStreamIndex);
    }

    [Fact]
    public async Task FlushAsync_And_ReloadFromDisk_RestoresStoredRecords()
    {
        // Nominal: Flush persists data to disk and a new store instance loads it correctly
        var userId = Guid.NewGuid();
        var itemId = Guid.NewGuid();

        using (var store1 = new TrackMemoryStore(_testDir, NullLogger<TrackMemoryStore>.Instance, autoFlushIntervalMs: 0))
        {
            store1.SaveSelection(userId, itemId, audioStreamIndex: 3, subtitleStreamIndex: null);
            await store1.FlushAsync();
        }

        using (var store2 = new TrackMemoryStore(_testDir, NullLogger<TrackMemoryStore>.Instance, autoFlushIntervalMs: 0))
        {
            var reloaded = store2.GetSelection(userId, itemId);
            Assert.NotNull(reloaded);
            Assert.Equal(3, reloaded.AudioStreamIndex);
            Assert.Null(reloaded.SubtitleStreamIndex);
        }
    }

    [Fact]
    public void Clear_RemovesAllEntries()
    {
        // Boundary: Clearing empty or populated store
        using var store = new TrackMemoryStore(_testDir, NullLogger<TrackMemoryStore>.Instance, autoFlushIntervalMs: 0);
        var userId = Guid.NewGuid();
        var itemId = Guid.NewGuid();

        store.SaveSelection(userId, itemId, audioStreamIndex: 1, subtitleStreamIndex: 1);
        Assert.NotNull(store.GetSelection(userId, itemId));

        store.Clear();
        Assert.Null(store.GetSelection(userId, itemId));
    }
}
