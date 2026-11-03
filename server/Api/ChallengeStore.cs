using System.Collections.Concurrent;

namespace YfgDesk.Server.Api;

/// <summary>
/// Bounded in-memory store of enrollment challenges (nonce + associated public key).
/// Capped at <c>MaxEntries</c>; expired entries are pruned lazily on every access.
/// </summary>
public sealed class ChallengeStore
{
    public sealed record Challenge(
        string ChallengeId,
        string Nonce,
        string PublicKeySpki,
        string PublicKeyDigest,
        string IssuedForIp,
        DateTime ExpiresAt);

    private readonly ConcurrentDictionary<string, Challenge> _store = new();
    private readonly int _maxEntries;
    private readonly TimeSpan _ttl;

    public ChallengeStore(int maxEntries = 4096, int ttlSeconds = 60)
    {
        _maxEntries = maxEntries;
        _ttl = TimeSpan.FromSeconds(ttlSeconds);
    }

    /// <summary>
    /// Insert a new challenge. Returns false if the store is full.
    /// </summary>
    public bool TryAdd(Challenge c)
    {
        PruneExpired();
        if (_store.Count >= _maxEntries)
        {
            // Refuse new entries until cleanup happens.
            PruneExpired();
            if (_store.Count >= _maxEntries) return false;
        }
        return _store.TryAdd(c.ChallengeId, c);
    }

    /// <summary>
    /// Atomically consume a challenge by ID. Returns null if missing or expired.
    /// </summary>
    public Challenge? TryConsume(string challengeId)
    {
        PruneExpired();
        if (_store.TryRemove(challengeId, out var c))
        {
            if (c.ExpiresAt < DateTime.UtcNow) return null;
            return c;
        }
        return null;
    }

    /// <summary>
    /// Drop all expired entries. Called periodically by the sweep service too.
    /// </summary>
    public int PruneExpired()
    {
        var nowUtc = DateTime.UtcNow;
        var removed = 0;
        foreach (var kv in _store)
        {
            if (kv.Value.ExpiresAt < nowUtc)
            {
                if (_store.TryRemove(kv.Key, out _)) removed++;
            }
        }
        return removed;
    }

    public int Count => _store.Count;
}
