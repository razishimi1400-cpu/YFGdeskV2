using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace YfgDesk.Client.Services;

/// <summary>
/// A single LAN discovery entry: <c>deviceId → (ip, port, lastSeen, displayName)</c>.
/// </summary>
public sealed class DiscoveryEntry
{
    public string DeviceId { get; set; } = string.Empty;
    public string Ip { get; set; } = string.Empty;
    public int Port { get; set; } = 49153;
    public string DisplayName { get; set; } = string.Empty;
    /// <summary>Unix epoch ms of last sighting.</summary>
    public long LastSeenMs { get; set; }
    /// <summary>Source of the entry (cache, broadcast, mdns, server-lookup).</summary>
    public string Source { get; set; } = "cache";
}

/// <summary>
/// A recent-target-id entry: just remembers IDs the user has typed recently.
/// </summary>
public sealed class RecentIdEntry
{
    public string DeviceId { get; set; } = string.Empty;
    public string? DisplayName { get; set; }
    public long LastUsedMs { get; set; }
}

/// <summary>
/// On-disk cache of LAN-discovered devices and recent target IDs.
/// Stored at <c>%APPDATA%\YFGDESK\discovery.cache</c> on Windows and
/// <c>~/.config/yfgdesk/discovery.cache</c> on Linux.
///
/// Format: JSON object with <c>devices</c> + <c>recent</c> arrays.
///
/// Thread-safe (every public method takes a lock). Writes are atomic
/// (write-temp-then-rename).
/// </summary>
public sealed class LocalCache
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly string _cacheFilePath;
    private readonly object _lock = new();
    private readonly Func<long> _clock;
    private readonly int _ttlSec;

    private Dictionary<string, DiscoveryEntry> _devices = new();
    private List<RecentIdEntry> _recent = new();

    public LocalCache(string cacheFilePath, int ttlSec = 60, Func<long>? clock = null)
    {
        _cacheFilePath = cacheFilePath ?? throw new ArgumentNullException(nameof(cacheFilePath));
        _ttlSec = ttlSec;
        _clock = clock ?? (() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        TryLoadFromDisk();
    }

    /// <summary>
    /// Returns the default cache file path for the current OS.
    /// Windows: <c>%APPDATA%\YFGDESK\discovery.cache</c>
    /// Linux/macOS: <c>~/.config/yfgdesk/discovery.cache</c>
    /// </summary>
    public static string DefaultCachePath()
    {
        string dir;
        if (OperatingSystem.IsWindows())
        {
            dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "YFGDESK");
        }
        else
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            dir = Path.Combine(home, ".config", "yfgdesk");
        }
        return Path.Combine(dir, "discovery.cache");
    }

    /// <summary>
    /// Lookup a device in the cache. Returns null if missing or stale
    /// (older than TTL).
    /// </summary>
    public DiscoveryEntry? LookupDevice(string deviceId)
    {
        if (string.IsNullOrEmpty(deviceId)) return null;
        lock (_lock)
        {
            if (!_devices.TryGetValue(deviceId, out var entry)) return null;
            var nowMs = _clock();
            var ageSec = (nowMs - entry.LastSeenMs) / 1000.0;
            if (ageSec > _ttlSec) return null;
            return new DiscoveryEntry
            {
                DeviceId = entry.DeviceId,
                Ip = entry.Ip,
                Port = entry.Port,
                DisplayName = entry.DisplayName,
                LastSeenMs = entry.LastSeenMs,
                Source = "cache",
            };
        }
    }

    /// <summary>
    /// Upsert a discovery entry and persist to disk. Caller provides the
    /// <paramref name="lastSeenMs"/>; passing 0 means "now".
    /// </summary>
    public void Upsert(DiscoveryEntry entry, long lastSeenMs = 0)
    {
        if (string.IsNullOrEmpty(entry.DeviceId)) return;
        lock (_lock)
        {
            var e = new DiscoveryEntry
            {
                DeviceId = entry.DeviceId,
                Ip = entry.Ip,
                Port = entry.Port,
                DisplayName = entry.DisplayName,
                LastSeenMs = lastSeenMs == 0 ? _clock() : lastSeenMs,
                Source = entry.Source,
            };
            _devices[e.DeviceId] = e;
            PersistToDiskUnsafe();
        }
    }

    /// <summary>
    /// Remove all stale entries from the cache and persist.
    /// </summary>
    public void Prune()
    {
        lock (_lock)
        {
            var nowMs = _clock();
            var keep = new Dictionary<string, DiscoveryEntry>();
            foreach (var (id, e) in _devices)
            {
                if ((nowMs - e.LastSeenMs) / 1000.0 <= _ttlSec) keep[id] = e;
            }
            if (keep.Count != _devices.Count)
            {
                _devices = keep;
                PersistToDiskUnsafe();
            }
        }
    }

    /// <summary>
    /// Record that the user used a target ID (recent list, capped at 16).
    /// </summary>
    public void RecordRecent(string deviceId, string? displayName = null)
    {
        if (string.IsNullOrEmpty(deviceId)) return;
        lock (_lock)
        {
            var nowMs = _clock();
            _recent.RemoveAll(r => r.DeviceId == deviceId);
            _recent.Insert(0, new RecentIdEntry
            {
                DeviceId = deviceId,
                DisplayName = displayName,
                LastUsedMs = nowMs,
            });
            if (_recent.Count > 16) _recent.RemoveRange(16, _recent.Count - 16);
            PersistToDiskUnsafe();
        }
    }

    public IReadOnlyList<RecentIdEntry> RecentIds()
    {
        lock (_lock) return _recent.ToList();
    }

    public IReadOnlyList<DiscoveryEntry> AllDevices()
    {
        lock (_lock) return _devices.Values.ToList();
    }

    public void Clear()
    {
        lock (_lock)
        {
            _devices.Clear();
            _recent.Clear();
            PersistToDiskUnsafe();
        }
    }

    // ---- internal helpers ----

    private sealed class CacheFile
    {
        [JsonPropertyName("devices")] public List<DiscoveryEntry> Devices { get; set; } = new();
        [JsonPropertyName("recent")] public List<RecentIdEntry> Recent { get; set; } = new();
    }

    private void TryLoadFromDisk()
    {
        try
        {
            if (!File.Exists(_cacheFilePath)) return;
            var json = File.ReadAllText(_cacheFilePath);
            var parsed = JsonSerializer.Deserialize<CacheFile>(json, JsonOpts);
            if (parsed is null) return;
            _devices = parsed.Devices
                .Where(d => !string.IsNullOrEmpty(d.DeviceId))
                .ToDictionary(d => d.DeviceId, d => d);
            _recent = parsed.Recent ?? new();
        }
        catch
        {
            // Corrupt or unreadable cache — start fresh.
            _devices = new();
            _recent = new();
        }
    }

    private void PersistToDiskUnsafe()
    {
        try
        {
            var dir = Path.GetDirectoryName(_cacheFilePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            var file = new CacheFile
            {
                Devices = _devices.Values.ToList(),
                Recent = _recent.ToList(),
            };
            var json = JsonSerializer.Serialize(file, JsonOpts);

            // atomic write: temp file + replace
            var tmp = _cacheFilePath + ".tmp";
            File.WriteAllText(tmp, json);
            if (File.Exists(_cacheFilePath))
                File.Replace(tmp, _cacheFilePath, destinationBackupFileName: null);
            else
                File.Move(tmp, _cacheFilePath);

            // Best-effort 0600 on Unix.
            if (!OperatingSystem.IsWindows())
            {
                try { File.SetUnixFileMode(_cacheFilePath, UnixFileMode.UserRead | UnixFileMode.UserWrite); }
                catch { /* not all filesystems support this */ }
            }
        }
        catch
        {
            // Cache is best-effort. Don't surface write errors to the user.
        }
    }
}
