using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace YouTubeDownloaderBot.Services;

/// <summary>
/// A persistent on-disk cache for the most recently downloaded media files.
/// Keeps at most <see cref="MaxEntries"/> entries globally (shared across all users),
/// evicting the oldest entries first. Keyed by URL + download type + quality.
/// </summary>
public sealed class DownloadCacheService
{
    private const int MaxEntries = 5;

    private readonly string _cacheDirectory;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly ConcurrentDictionary<string, CacheEntry> _index = new();
    private readonly ILogger<DownloadCacheService> _logger;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public DownloadCacheService(ILogger<DownloadCacheService> logger)
    {
        _logger = logger;
        _cacheDirectory = Path.Combine(Path.GetTempPath(), "YouTubeDownloaderBot", "cache");
        Directory.CreateDirectory(_cacheDirectory);
        LoadIndexFromDisk();
    }

    /// <summary>
    /// Looks up a cached file for the given key. Returns the cached file path
    /// if a valid (existing) entry is found; otherwise null.
    /// </summary>
    public async Task<string?> TryGetAsync(string url, string type, string quality)
    {
        var key = BuildKey(url, type, quality);

        if (!_index.TryGetValue(key, out var entry))
            return null;

        if (!File.Exists(entry.FilePath))
        {
            // Stale entry — file was removed out of band; drop it from the index.
            _index.TryRemove(key, out _);
            TryDeleteMeta(entry);
            return null;
        }

        // Bump recency so frequently-requested items survive eviction.
        entry.LastAccessedUtc = DateTime.UtcNow;
        await PersistMetaAsync(entry);
        _logger.LogDebug("Cache HIT for {Key}", key);
        return entry.FilePath;
    }

    /// <summary>
    /// Stores a freshly produced file in the cache. The source file is copied
    /// (not moved) so callers can still use/delete their working copy.
    /// Enforces the max-entries limit by evicting the oldest entries.
    /// </summary>
    public async Task StoreAsync(
        string sourceFilePath,
        string url,
        string type,
        string quality,
        string originalFileName)
    {
        if (!File.Exists(sourceFilePath))
            return;

        var key = BuildKey(url, type, quality);

        await _lock.WaitAsync();
        try
        {
            // If this exact key is already cached, refresh its content in place.
            if (_index.TryGetValue(key, out var existing) && File.Exists(existing.FilePath))
            {
                File.Copy(sourceFilePath, existing.FilePath, overwrite: true);
                existing.LastAccessedUtc = DateTime.UtcNow;
                existing.CreatedUtc = DateTime.UtcNow;
                await PersistMetaAsync(existing);
                _logger.LogDebug("Cache REFRESHED for {Key}", key);
                return;
            }

            var id = Guid.NewGuid().ToString("N");
            var ext = Path.GetExtension(originalFileName);
            var cachedFilePath = Path.Combine(_cacheDirectory, $"{id}{ext}");

            File.Copy(sourceFilePath, cachedFilePath);

            var entry = new CacheEntry
            {
                Key = key,
                FilePath = cachedFilePath,
                MetaPath = Path.Combine(_cacheDirectory, $"{id}.meta.json"),
                Url = url,
                Type = type,
                Quality = quality,
                OriginalFileName = originalFileName,
                CreatedUtc = DateTime.UtcNow,
                LastAccessedUtc = DateTime.UtcNow
            };

            _index[key] = entry;
            await PersistMetaAsync(entry);

            EvictIfNeeded();
            _logger.LogDebug("Cache STORED {Key} ({Count}/{Max})", key, _index.Count, MaxEntries);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Removes cache entries beyond <see cref="MaxEntries"/>, oldest first.
    /// Must be called under <see cref="_lock"/>.
    /// </summary>
    private void EvictIfNeeded()
    {
        while (_index.Count > MaxEntries)
        {
            var oldest = _index.Values
                .OrderBy(e => e.LastAccessedUtc)
                .FirstOrDefault();

            if (oldest == null)
                break;

            _index.TryRemove(oldest.Key, out _);
            TryDeleteFile(oldest.FilePath);
            TryDeleteMeta(oldest);
            _logger.LogDebug("Cache EVICTED {Key}", oldest.Key);
        }
    }

    private static string BuildKey(string url, string type, string quality)
        => $"{type}|{quality}|{url}";

    private Task PersistMetaAsync(CacheEntry entry)
    {
        try
        {
            var json = JsonSerializer.Serialize(entry, JsonOptions);
            File.WriteAllText(entry.MetaPath, json);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to persist cache meta for {Key}", entry.Key);
        }
        return Task.CompletedTask;
    }

    private void TryDeleteMeta(CacheEntry entry) => TryDeleteFile(entry.MetaPath);

    private void TryDeleteFile(string? path)
    {
        if (string.IsNullOrEmpty(path)) return;
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception ex) { _logger.LogWarning(ex, "Failed to delete {Path}", path); }
    }

    /// <summary>
    /// Rebuilds the in-memory index from .meta.json sidecar files on disk,
    /// so the cache survives process restarts.
    /// </summary>
    private void LoadIndexFromDisk()
    {
        try
        {
            foreach (var metaFile in Directory.GetFiles(_cacheDirectory, "*.meta.json"))
            {
                try
                {
                    var json = File.ReadAllText(metaFile);
                    var entry = JsonSerializer.Deserialize<CacheEntry>(json, JsonOptions);
                    if (entry == null || string.IsNullOrEmpty(entry.Key)) continue;

                    if (!File.Exists(entry.FilePath))
                    {
                        TryDeleteFile(metaFile);
                        continue;
                    }

                    entry.MetaPath = metaFile;
                    _index[entry.Key] = entry;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to read cache meta {File}", metaFile);
                }
            }

            // Drop any excess entries left over from previous runs.
            EvictIfNeeded();
            _logger.LogInformation("Cache loaded with {Count} entries", _index.Count);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load cache index from disk");
        }
    }

    private sealed class CacheEntry
    {
        public string Key { get; set; } = "";
        public string FilePath { get; set; } = "";
        [JsonIgnore] public string MetaPath { get; set; } = "";
        public string Url { get; set; } = "";
        public string Type { get; set; } = "";
        public string Quality { get; set; } = "";
        public string OriginalFileName { get; set; } = "";
        public DateTime CreatedUtc { get; set; }
        public DateTime LastAccessedUtc { get; set; }
    }
}
