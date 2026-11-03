using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Net.WebSockets;
using YfgDesk.Server.Data;
using YfgDesk.Shared.Protocol;

namespace YfgDesk.Server.Signaling;

/// <summary>
/// Represents a single live WebSocket connection for one device.
/// </summary>
public sealed class SignalingConnection
{
    public required string DeviceId { get; init; }
    public required WebSocket Socket { get; init; }
    public required string Ip { get; init; }
    public string Role { get; set; } = "VIEWER";
    public string DisplayName { get; set; } = string.Empty;
    public DateTime ConnectedAt { get; init; } = DateTime.UtcNow;
    public DateTime LastHeartbeatAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Per-connection send lock (WebSocket.SendAsync is not thread-safe).
    /// </summary>
    public SemaphoreSlim SendLock { get; } = new(1, 1);
}

/// <summary>
/// In-memory per-session state: peers, role mapping, replay window, lifecycle timestamps.
/// </summary>
public sealed class SignalingSessionState
{
    public required string SessionId { get; init; }
    public required string HostDeviceId { get; init; }
    public required string ViewerDeviceId { get; init; }
    public SessionState State { get; set; } = SessionState.Pending;
    public int RequestedPermissions { get; init; }
    public int GrantedPermissions { get; set; }
    public VideoPreference? VideoPref { get; init; }
    public string ViewerDisplayName { get; init; } = string.Empty;
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
    public DateTime AcceptedAt { get; set; }
    public DateTime LastHeartbeatAt { get; set; } = DateTime.UtcNow;
    public DateTime ExpiresAt { get; init; }
    public DateTime? EndedAt { get; set; }
    public string? EndReason { get; set; }

    /// <summary>
    /// Anti-replay window: messageIds seen on this session (bounded LRU).
    /// </summary>
    private readonly LinkedList<string> _recent = new();
    private readonly HashSet<string> _set = new();
    private readonly int _max;

    public SignalingSessionState(int replayWindowSize)
    {
        _max = replayWindowSize;
    }

    /// <summary>
    /// Atomically try to record a messageId. Returns false if it has been seen before.
    /// </summary>
    public bool TryRecordMessageId(string messageId)
    {
        lock (this)
        {
            if (_set.Contains(messageId)) return false;
            if (_recent.Count >= _max)
            {
                var oldest = _recent.First!.Value;
                _recent.RemoveFirst();
                _set.Remove(oldest);
            }
            _recent.AddLast(messageId);
            _set.Add(messageId);
            return true;
        }
    }
}

/// <summary>
/// Holds all live WebSocket connections and signaling sessions.
/// </summary>
public sealed class SignalingSessionStore
{
    private readonly ConcurrentDictionary<string, SignalingConnection> _connections = new();
    private readonly ConcurrentDictionary<string, SignalingSessionState> _sessions = new();
    private readonly YfgDeskOptions _opts;

    public SignalingSessionStore(YfgDeskOptions opts)
    {
        _opts = opts;
    }

    // Connections ------------------------------------------------------------

    public bool TryAddConnection(SignalingConnection conn) => _connections.TryAdd(conn.DeviceId, conn);

    public bool TryGetConnection(string deviceId, [NotNullWhen(true)] out SignalingConnection? conn) =>
        _connections.TryGetValue(deviceId, out conn);

    public bool TryRemoveConnection(string deviceId, [NotNullWhen(true)] out SignalingConnection? conn) =>
        _connections.TryRemove(deviceId, out conn);

    public IEnumerable<SignalingConnection> AllConnections => _connections.Values;

    // Sessions ---------------------------------------------------------------

    public SignalingSessionState CreateSession(string sessionId, string hostId, string viewerId,
        int requestedPerms, VideoPreference videoPref, string viewerDisplayName, DateTime expiresAt)
    {
        var s = new SignalingSessionState(_opts.ReplayWindowSize)
        {
            SessionId = sessionId,
            HostDeviceId = hostId,
            ViewerDeviceId = viewerId,
            RequestedPermissions = requestedPerms,
            VideoPref = videoPref,
            ViewerDisplayName = viewerDisplayName,
            ExpiresAt = expiresAt,
        };
        _sessions[sessionId] = s;
        return s;
    }

    public bool TryGetSession(string sessionId, [NotNullWhen(true)] out SignalingSessionState? session) =>
        _sessions.TryGetValue(sessionId, out session);

    public bool TryRemoveSession(string sessionId, [NotNullWhen(true)] out SignalingSessionState? session) =>
        _sessions.TryRemove(sessionId, out session);

    public IEnumerable<SignalingSessionState> AllSessions => _sessions.Values;

    /// <summary>
    /// Sweep all sessions: expire pending (consent timeout), expire idle, expire max-age.
    /// Returns the sessions that were torn down.
    /// </summary>
    public List<SignalingSessionState> Sweep()
    {
        var now = DateTime.UtcNow;
        var expired = new List<SignalingSessionState>();
        foreach (var kv in _sessions)
        {
            var s = kv.Value;
            bool isExpired = false;
            string reason = "unknown";
            if (s.State == SessionState.Pending && now > s.ExpiresAt)
            {
                isExpired = true; reason = "consent_timeout";
            }
            else if ((s.State == SessionState.Active || s.State == SessionState.Accepted)
                     && (now - s.LastHeartbeatAt).TotalSeconds > _opts.SessionIdleSeconds)
            {
                isExpired = true; reason = "idle_timeout";
            }
            else if ((now - s.CreatedAt).TotalSeconds > _opts.SessionMaxLifetimeSeconds)
            {
                isExpired = true; reason = "max_lifetime";
            }
            if (isExpired && _sessions.TryRemove(kv.Key, out var removed))
            {
                removed.EndedAt = now;
                removed.EndReason = reason;
                removed.State = reason == "consent_timeout" ? SessionState.TimedOut : SessionState.Ended;
                expired.Add(removed);
            }
        }
        return expired;
    }

    /// <summary>
    /// Map a session ID + role to the peer's role.
    /// </summary>
    public SignalingConnection? PeerOf(SignalingSessionState s, string myDeviceId)
    {
        var peerId = myDeviceId == s.HostDeviceId ? s.ViewerDeviceId : s.HostDeviceId;
        return TryGetConnection(peerId, out var peer) ? peer : null;
    }
}
