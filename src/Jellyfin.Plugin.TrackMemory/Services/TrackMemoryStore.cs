using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.TrackMemory.Domain;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.TrackMemory.Services;

/// <summary>
/// Thread-safe in-memory cache backed by asynchronous JSON file persistence.
/// </summary>
public class TrackMemoryStore : ITrackMemoryStore, IDisposable
{
    private readonly string _storagePath;
    private readonly ILogger<TrackMemoryStore> _logger;
    private readonly ConcurrentDictionary<(Guid UserId, Guid ItemId), TrackSelection> _cache = new();
    private readonly SemaphoreSlim _fileLock = new(1, 1);
    private readonly Timer? _flushTimer;
    private bool _isDirty;
    private bool _disposed;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    /// <summary>
    /// Initializes a new instance of the <see cref="TrackMemoryStore"/> class.
    /// </summary>
    /// <param name="storageDirectory">The directory where data should be stored.</param>
    /// <param name="logger">The logger instance.</param>
    /// <param name="autoFlushIntervalMs">Interval in ms for automatic disk flush (default 10000ms = 10s).</param>
    public TrackMemoryStore(string storageDirectory, ILogger<TrackMemoryStore> logger, int autoFlushIntervalMs = 10000)
    {
        _logger = logger;
        Directory.CreateDirectory(storageDirectory);
        _storagePath = Path.Combine(storageDirectory, "track-memory.json");

        LoadFromDisk();

        if (autoFlushIntervalMs > 0)
        {
            _flushTimer = new Timer(async _ => await OnAutoFlushAsync().ConfigureAwait(false), null, autoFlushIntervalMs, autoFlushIntervalMs);
        }
    }

    /// <inheritdoc />
    public TrackSelection? GetSelection(Guid userId, Guid itemId)
    {
        if (_cache.TryGetValue((userId, itemId), out var selection))
        {
            return selection;
        }

        return null;
    }

    /// <inheritdoc />
    public void SaveSelection(Guid userId, Guid itemId, int? audioStreamIndex, int? subtitleStreamIndex)
    {
        var key = (userId, itemId);

        _cache.AddOrUpdate(
            key,
            _ =>
            {
                _isDirty = true;
                _logger.LogDebug("Recorded new track selection for user {UserId}, item {ItemId}: Audio={Audio}, Sub={Sub}", userId, itemId, audioStreamIndex, subtitleStreamIndex);
                return new TrackSelection(itemId, audioStreamIndex, subtitleStreamIndex);
            },
            (_, existing) =>
            {
                if (existing.AudioStreamIndex != audioStreamIndex || existing.SubtitleStreamIndex != subtitleStreamIndex)
                {
                    existing.AudioStreamIndex = audioStreamIndex;
                    existing.SubtitleStreamIndex = subtitleStreamIndex;
                    existing.UpdatedAtUtc = DateTime.UtcNow;
                    _isDirty = true;
                    _logger.LogDebug("Updated track selection for user {UserId}, item {ItemId}: Audio={Audio}, Sub={Sub}", userId, itemId, audioStreamIndex, subtitleStreamIndex);
                }
                return existing;
            });
    }

    /// <inheritdoc />
    public async Task FlushAsync()
    {
        if (!_isDirty)
        {
            return;
        }

        await _fileLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!_isDirty)
            {
                return;
            }

            var entries = new List<StoredRecord>();
            foreach (var kvp in _cache)
            {
                entries.Add(new StoredRecord
                {
                    UserId = kvp.Key.UserId,
                    ItemId = kvp.Key.ItemId,
                    AudioStreamIndex = kvp.Value.AudioStreamIndex,
                    SubtitleStreamIndex = kvp.Value.SubtitleStreamIndex,
                    UpdatedAtUtc = kvp.Value.UpdatedAtUtc
                });
            }

            var tempPath = _storagePath + ".tmp";
            var json = JsonSerializer.Serialize(entries, JsonOptions);
            await File.WriteAllTextAsync(tempPath, json).ConfigureAwait(false);

            if (File.Exists(_storagePath))
            {
                File.Replace(tempPath, _storagePath, null);
            }
            else
            {
                File.Move(tempPath, _storagePath);
            }

            _isDirty = false;
            _logger.LogDebug("Successfully flushed {Count} track memory records to disk at {Path}", entries.Count, _storagePath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to flush track memory records to disk at {Path}", _storagePath);
        }
        finally
        {
            _fileLock.Release();
        }
    }

    /// <inheritdoc />
    public void Clear()
    {
        _cache.Clear();
        _isDirty = true;
    }

    private void LoadFromDisk()
    {
        if (!File.Exists(_storagePath))
        {
            _logger.LogInformation("No existing track memory store found at {Path}. Starting empty.", _storagePath);
            return;
        }

        try
        {
            var json = File.ReadAllText(_storagePath);
            var records = JsonSerializer.Deserialize<List<StoredRecord>>(json);
            if (records != null)
            {
                foreach (var rec in records)
                {
                    var selection = new TrackSelection(rec.ItemId, rec.AudioStreamIndex, rec.SubtitleStreamIndex)
                    {
                        UpdatedAtUtc = rec.UpdatedAtUtc
                    };
                    _cache[(rec.UserId, rec.ItemId)] = selection;
                }
                _logger.LogInformation("Loaded {Count} track memory records from {Path}", records.Count, _storagePath);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error reading track memory records from {Path}. Starting fresh in-memory.", _storagePath);
        }
    }

    private async Task OnAutoFlushAsync()
    {
        if (_isDirty)
        {
            await FlushAsync().ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _flushTimer?.Dispose();

        try
        {
            FlushAsync().GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error flushing track memory on Dispose");
        }

        _fileLock.Dispose();
        _disposed = true;
        GC.SuppressFinalize(this);
    }

    private sealed class StoredRecord
    {
        public Guid UserId { get; set; }
        public Guid ItemId { get; set; }
        public int? AudioStreamIndex { get; set; }
        public int? SubtitleStreamIndex { get; set; }
        public DateTime UpdatedAtUtc { get; set; }
    }
}
