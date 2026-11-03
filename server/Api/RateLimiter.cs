using System.Collections.Concurrent;

namespace YfgDesk.Server.Api;

/// <summary>
/// In-memory sliding-window rate limiter. Returns false when <paramref name="limit"/>
/// events have occurred within the trailing 60 seconds for the given key.
/// </summary>
public sealed class RateLimiter
{
    private readonly ConcurrentDictionary<string, Bucket> _buckets = new();
    private readonly TimeSpan _window = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Check whether an action by <paramref name="key"/> is permitted, and if so record a hit.
    /// Returns true if allowed (and recorded), false if rate-limited.
    /// </summary>
    public bool TryConsume(string key, int limit)
    {
        if (string.IsNullOrEmpty(key)) return false;
        if (limit <= 0) return false;
        var nowUtc = DateTime.UtcNow;
        var bucket = _buckets.GetOrAdd(key, _ => new Bucket());
        lock (bucket)
        {
            // Drop expired entries.
            var cutoff = nowUtc - _window;
            while (bucket.Events.Count > 0 && bucket.Events.Peek() < cutoff)
                bucket.Events.Dequeue();
            if (bucket.Events.Count >= limit)
                return false;
            bucket.Events.Enqueue(nowUtc);
            return true;
        }
    }

    /// <summary>
    /// Periodically trim empty buckets so memory does not grow unbounded.
    /// </summary>
    public int Trim()
    {
        var cutoff = DateTime.UtcNow - _window;
        var removed = 0;
        foreach (var kv in _buckets)
        {
            lock (kv.Value)
            {
                while (kv.Value.Events.Count > 0 && kv.Value.Events.Peek() < cutoff)
                    kv.Value.Events.Dequeue();
                if (kv.Value.Events.Count == 0)
                {
                    if (_buckets.TryRemove(kv.Key, out _)) removed++;
                }
            }
        }
        return removed;
    }

    private sealed class Bucket
    {
        public Queue<DateTime> Events { get; } = new();
    }
}

/// <summary>
/// Common rate-limit bucket keys.
/// </summary>
public static class RateLimitKeys
{
    public static string EnrollByIp(string ip) => $"enroll:ip:{ip}";
    public static string WsByDevice(string deviceId) => $"ws:dev:{deviceId}";
}
