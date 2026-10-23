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
        return GetSelection(userId, itemId, null);
    }

    /// <inheritdoc />
    public TrackSelection? GetSelection(Guid userId, Guid itemId, Guid? seriesId = null)
    {
        if (_cache.TryGetValue((userId, itemId), out var selection))
        {
            return selection;
        }

        if (seriesId.HasValue && seriesId.Value != Guid.Empty && _cache.TryGetValue((userId, seriesId.Value), out var seriesSelection))
        {
            return seriesSelection;
        }

        return null;
    }

    /// <inheritdoc />
    public void SaveSelection(Guid userId, Guid itemId, int? audioStreamIndex, int? subtitleStreamIndex)
    {
        SaveSelection(userId, itemId, null, audioStreamIndex, subtitleStreamIndex, null, null, subtitleStreamIndex == -1);
    }

    /// <inheritdoc />
    public void SaveSelection(
        Guid userId,
        Guid itemId,
        Guid? seriesId,
        int? audioStreamIndex,
        int? subtitleStreamIndex,
        string? audioLanguage = null,
        string? subtitleLanguage = null,
        bool subtitleDisabled = false)
    {
        bool effectiveDisabled = subtitleDisabled || subtitleStreamIndex == -1;

        SaveSingleSelection(userId, itemId, audioStreamIndex, subtitleStreamIndex, audioLanguage, subtitleLanguage, effectiveDisabled);

        if (seriesId.HasValue && seriesId.Value != Guid.Empty && seriesId.Value != itemId)
        {
            SaveSingleSelection(userId, seriesId.Value, audioStreamIndex, subtitleStreamIndex, audioLanguage, subtitleLanguage, effectiveDisabled);
        }
    }

    private void SaveSingleSelection(
        Guid userId,
        Guid targetId,
        int? audioStreamIndex,
        int? subtitleStreamIndex,
        string? audioLanguage,
        string? subtitleLanguage,
        bool subtitleDisabled)
    {
        var key = (userId, targetId);

        _cache.AddOrUpdate(
            key,
            _ =>
            {
                _isDirty = true;
                _logger.LogDebug(
                    "Recorded new track selection for user {UserId}, target {TargetId}: Audio={Audio} ({AudioLang}), Sub={Sub} ({SubLang}), SubDisabled={SubDisabled}",
                    userId,
                    targetId,
                    audioStreamIndex,
                    audioLanguage,
                    subtitleStreamIndex,
                    subtitleLanguage,
                    subtitleDisabled);

                return new TrackSelection(targetId, audioStreamIndex, subtitleStreamIndex, audioLanguage, subtitleLanguage, subtitleDisabled);
            },
            (_, existing) =>
            {
                var newAudioIndex = audioStreamIndex ?? existing.AudioStreamIndex;
                var newSubIndex = subtitleStreamIndex ?? existing.SubtitleStreamIndex;
                var newAudioLang = !string.IsNullOrEmpty(audioLanguage) ? audioLanguage : existing.AudioLanguage;
                var newSubLang = subtitleDisabled ? null : (!string.IsNullOrEmpty(subtitleLanguage) ? subtitleLanguage : existing.SubtitleLanguage);

                bool changed = existing.AudioStreamIndex != newAudioIndex
                    || existing.SubtitleStreamIndex != newSubIndex
                    || !string.Equals(existing.AudioLanguage, newAudioLang, StringComparison.OrdinalIgnoreCase)
                    || !string.Equals(existing.SubtitleLanguage, newSubLang, StringComparison.OrdinalIgnoreCase)
                    || existing.SubtitleDisabled != subtitleDisabled;

                if (changed)
                {
                    _isDirty = true;
                    _logger.LogDebug(
                        "Updated track selection for user {UserId}, target {TargetId}: Audio={Audio} ({AudioLang}), Sub={Sub} ({SubLang}), SubDisabled={SubDisabled}",
                        userId,
                        targetId,
                        newAudioIndex,
                        newAudioLang,
                        newSubIndex,
                        newSubLang,
                        subtitleDisabled);

                    return new TrackSelection(targetId, newAudioIndex, newSubIndex, newAudioLang, newSubLang, subtitleDisabled);
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
                    AudioLanguage = kvp.Value.AudioLanguage,
                    SubtitleLanguage = kvp.Value.SubtitleLanguage,
                    SubtitleDisabled = kvp.Value.SubtitleDisabled,
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
                    var selection = new TrackSelection(
                        rec.ItemId,
                        rec.AudioStreamIndex,
                        rec.SubtitleStreamIndex,
                        rec.AudioLanguage,
                        rec.SubtitleLanguage,
                        rec.SubtitleDisabled)
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
        public string? AudioLanguage { get; set; }
        public string? SubtitleLanguage { get; set; }
        public bool SubtitleDisabled { get; set; }
        public DateTime UpdatedAtUtc { get; set; }
    }
}
