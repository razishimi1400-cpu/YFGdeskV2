using YfgDesk.Shared.Protocol;

namespace YfgDesk.Client.Services;

/// <summary>
/// Host-side consent state machine. Drives the UI prompt shown to the host
/// ("Accept incoming request?") and enforces the transition rules.
///
/// <code>
///   Idle ──Begin──► AwaitingConsent ──Accept──► Accepted ──Activate──► Active ──End──► Ended
///                       │                                                          ▲
///                       ├──Reject──► Rejected (terminal)                          │
///                       └──Expire──► TimedOut (terminal)                           │
///                                                                                  │
///   EmergencyStop (any non-terminal state) ──────────────────────────────────────┘
/// </code>
///
/// Thread-safe. All transitions are validated against the host's role and
/// the requested permissions; <see cref="Accept"/> is restricted to a strict
/// subset of the originally requested permissions.
/// </summary>
public sealed class HostConsentCoordinator
{
    private readonly object _lock = new();
    private State _state = State.Idle;
    private string? _activeSessionId;
    private int _requestedPermissions;
    private int _grantedPermissions;
    private long _awaitingSinceMs;
    private long _awaitingDeadlineMs;
    private VideoPreference? _videoPreference;
    private string? _viewerDisplayName;
    private string? _viewerId;

    public enum State
    {
        Idle,
        AwaitingConsent,
        Accepted,
        Rejected,
        TimedOut,
        Active,
        Ended,
    }

    /// <summary>Consent request window in milliseconds. Default 5 minutes.</summary>
    public long ConsentWindowMs { get; set; } = 5 * 60 * 1000;

    public State CurrentState
    {
        get { lock (_lock) return _state; }
    }

    public string? ActiveSessionId
    {
        get { lock (_lock) return _activeSessionId; }
    }

    public int GrantedPermissions
    {
        get { lock (_lock) return _grantedPermissions; }
    }

    public string? ViewerDisplayName
    {
        get { lock (_lock) return _viewerDisplayName; }
    }

    /// <summary>
    /// Begin awaiting consent for a fresh incoming request. Throws if the
    /// coordinator is already mid-session.
    /// </summary>
    /// <param name="sessionId">The session ID from the server.</param>
    /// <param name="viewerId">The viewer's device ID.</param>
    /// <param name="viewerDisplayName">Display name shown to the host.</param>
    /// <param name="permissions">Requested permissions (bitmask).</param>
    /// <param name="videoPreference">Viewer's video quality preference.</param>
    /// <param name="nowMs">Unix epoch ms.</param>
    /// <exception cref="InvalidOperationException">If already awaiting or active.</exception>
    public void Begin(
        string sessionId,
        string viewerId,
        string? viewerDisplayName,
        int permissions,
        VideoPreference? videoPreference,
        long nowMs)
    {
        if (string.IsNullOrEmpty(sessionId)) throw new ArgumentException("sessionId required", nameof(sessionId));
        if (string.IsNullOrEmpty(viewerId)) throw new ArgumentException("viewerId required", nameof(viewerId));

        lock (_lock)
        {
            if (_state is State.AwaitingConsent or State.Accepted or State.Active)
                throw new InvalidOperationException($"cannot Begin: state={_state}");

            _activeSessionId = sessionId;
            _viewerId = viewerId;
            _viewerDisplayName = viewerDisplayName;
            _requestedPermissions = permissions;
            _grantedPermissions = 0;
            _videoPreference = videoPreference;
            _awaitingSinceMs = nowMs;
            _awaitingDeadlineMs = nowMs + ConsentWindowMs;
            _state = State.AwaitingConsent;
        }
    }

    /// <summary>
    /// Accept the request with a (possibly reduced) subset of the originally
    /// requested permissions. <paramref name="grantedPermissions"/> must be a
    /// subset of <see cref="_requestedPermissions"/>.
    /// </summary>
    public bool Accept(int grantedPermissions, long nowMs)
    {
        lock (_lock)
        {
            if (_state != State.AwaitingConsent) return false;
            if (nowMs > _awaitingDeadlineMs)
            {
                _state = State.TimedOut;
                return false;
            }
            // Subset check: every granted bit must be in requested.
            if ((grantedPermissions & ~_requestedPermissions) != 0) return false;
            if (grantedPermissions == 0) return false; // must grant at least ViewScreen

            _grantedPermissions = grantedPermissions;
            _state = State.Accepted;
            return true;
        }
    }

    /// <summary>
    /// Reject the request. Terminal.
    /// </summary>
    public bool Reject()
    {
        lock (_lock)
        {
            if (_state != State.AwaitingConsent) return false;
            _grantedPermissions = 0;
            _state = State.Rejected;
            return true;
        }
    }

    /// <summary>
    /// Advance to Active (typically called when the first media packet flows).
    /// Only valid from Accepted.
    /// </summary>
    public bool Activate(long nowMs)
    {
        lock (_lock)
        {
            if (_state != State.Accepted) return false;
            _state = State.Active;
            return true;
        }
    }

    /// <summary>
    /// End the session. Terminal (from any non-terminal state).
    /// </summary>
    public bool End()
    {
        lock (_lock)
        {
            if (_state is State.Ended or State.Rejected or State.TimedOut) return false;
            _grantedPermissions = 0;
            _state = State.Ended;
            return true;
        }
    }

    /// <summary>
    /// Tick the consent deadline. If we're still awaiting and the deadline
    /// passed, transition to TimedOut.
    /// </summary>
    public bool Expire(long nowMs)
    {
        lock (_lock)
        {
            if (_state != State.AwaitingConsent) return false;
            if (nowMs <= _awaitingDeadlineMs) return false;
            _grantedPermissions = 0;
            _state = State.TimedOut;
            return true;
        }
    }

    /// <summary>
    /// Kill switch. Clears permissions and transitions to Ended from ANY
    /// non-terminal state. Idempotent.
    /// </summary>
    public void EmergencyStop()
    {
        lock (_lock)
        {
            if (_state is State.Ended) return;
            _grantedPermissions = 0;
            _requestedPermissions = 0;
            _state = State.Ended;
        }
    }

    /// <summary>
    /// Reset to Idle (e.g. after the UI closed the dialog).
    /// </summary>
    public void Reset()
    {
        lock (_lock)
        {
            _state = State.Idle;
            _activeSessionId = null;
            _viewerId = null;
            _viewerDisplayName = null;
            _grantedPermissions = 0;
            _requestedPermissions = 0;
            _videoPreference = null;
            _awaitingSinceMs = 0;
            _awaitingDeadlineMs = 0;
        }
    }

    /// <summary>
    /// Returns whether a given permission is currently granted.
    /// </summary>
    public bool HasPermission(Permissions perm)
    {
        lock (_lock)
        {
            if (_state != State.Active) return false;
            return (_grantedPermissions & (int)perm) != 0;
        }
    }

    public record Snapshot(
        State CurrentState,
        string? SessionId,
        string? ViewerId,
        string? ViewerDisplayName,
        int RequestedPermissions,
        int GrantedPermissions,
        long AwaitingSinceMs,
        long AwaitingDeadlineMs);

    public Snapshot GetSnapshot()
    {
        lock (_lock)
        {
            return new Snapshot(
                _state,
                _activeSessionId,
                _viewerId,
                _viewerDisplayName,
                _requestedPermissions,
                _grantedPermissions,
                _awaitingSinceMs,
                _awaitingDeadlineMs);
        }
    }
}
