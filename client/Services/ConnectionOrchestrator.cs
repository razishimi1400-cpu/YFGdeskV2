using System.Diagnostics;
using YfgDesk.Shared.Protocol;

namespace YfgDesk.Client.Services;

/// <summary>
/// The connection tier that ultimately carried the session.
/// </summary>
public enum ConnectionTier
{
    /// <summary>Tier 1: local cache + direct LAN TCP.</summary>
    DirectLan,
    /// <summary>Tier 2: LAN broadcast query + direct LAN TCP.</summary>
    LanDiscovered,
    /// <summary>Tier 3: P2P via ICE (host + srflx).</summary>
    P2p,
    /// <summary>Tier 4: TURN relay.</summary>
    Relay,
    /// <summary>All tiers failed.</summary>
    Failed,
}

/// <summary>
/// Result of <see cref="ConnectionOrchestrator.ConnectAsync"/>. A single
/// discriminated-union-like record carrying all possible fields; only the
/// fields relevant to <see cref="Tier"/> are populated.
/// </summary>
public sealed record ConnectionResult(
    ConnectionTier Tier,
    string TargetId,
    string? Ip = null,
    int? Port = null,
    IceServer[]? IceServers = null,
    string? TurnUsername = null,
    string? FailureReason = null,
    TimeSpan Elapsed = default);

/// <summary>
/// Tier-1 / Tier-2: probe a direct LAN TCP connection to (ip, port).
/// </summary>
public interface IDirectLanProbe
{
    Task<bool> TryConnectAsync(string ip, int port, CancellationToken ct = default);
}

/// <summary>
/// Tier-3: server lookup for the target's public IP / listen port / online state.
/// </summary>
public interface IServerLookupClient
{
    Task<LookupResponse?> LookupAsync(string targetId, CancellationToken ct = default);
}

public sealed record LookupResponse(
    string Ip,
    int? Port,
    bool Online,
    DateTime LastSeen,
    string[] PublicIps,
    string[] ListenPorts,
    string DeviceId,
    string DisplayName);

/// <summary>
/// Tier-3 step 2 / Tier-4: gather ICE candidates and try P2P (host + srflx).
/// Implementations should attempt to establish a WebRTC peer connection
/// using the given ICE servers, returning <c>true</c> on success within
/// the supplied cancellation token.
/// </summary>
public interface IP2pEstablisher
{
    Task<bool> TryEstablishAsync(string targetId, IceServer[] iceServers, CancellationToken ct = default);
}

/// <summary>
/// Tier-4: fetch short-lived TURN credentials from the server.
/// </summary>
public interface ITurnClient
{
    Task<TurnBundle?> GetCredentialsAsync(CancellationToken ct = default);
}

public sealed record TurnBundle(
    string Username,
    string Credential,
    string[] Urls,
    DateTime ExpiresAt);

/// <summary>
/// <para>
/// Coordinator for the three-tier connection priority described in
/// ARCHITECTURE.md §2.3:
/// </para>
/// <list type="number">
///   <item><b>Tier 1</b> — LAN cache lookup. If <paramref name="targetId"/> is in the local cache AND fresh (&lt; 60s), probe a direct LAN TCP connection. On success → <see cref="ConnectionResult.DirectLan"/>.</item>
///   <item><b>Tier 2</b> — Broadcast UDP / mDNS query for 3s. If a peer responds, cache + probe direct LAN TCP. On success → <see cref="ConnectionResult.LanDiscovered"/>.</item>
///   <item><b>Tier 3</b> — Server REST lookup. If <c>online: true</c>, gather ICE candidates and try P2P. On success → <see cref="ConnectionResult.P2p"/>.</item>
///   <item><b>Tier 4</b> — TURN relay fallback. Fetch short-lived credentials and use TURN. On success → <see cref="ConnectionResult.Relay"/>.</item>
/// </list>
/// <para>
/// On any tier failure, log + fall through to the next tier. On all failures,
/// surface the last error.
/// </para>
/// </summary>
public sealed class ConnectionOrchestrator
{
    private readonly LocalCache _cache;
    private readonly DiscoveryClient _discovery;
    private readonly IServerLookupClient _serverLookup;
    private readonly ITurnClient _turnClient;
    private readonly IP2pEstablisher _p2p;
    private readonly IDirectLanProbe _directLanProbe;
    private readonly ClientOptions _opts;
    private readonly ILogger _log;

    public ConnectionOrchestrator(
        LocalCache cache,
        DiscoveryClient discovery,
        IServerLookupClient serverLookup,
        ITurnClient turnClient,
        IP2pEstablisher p2p,
        IDirectLanProbe directLanProbe,
        ClientOptions opts,
        ILogger? log = null)
    {
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        _discovery = discovery ?? throw new ArgumentNullException(nameof(discovery));
        _serverLookup = serverLookup ?? throw new ArgumentNullException(nameof(serverLookup));
        _turnClient = turnClient ?? throw new ArgumentNullException(nameof(turnClient));
        _p2p = p2p ?? throw new ArgumentNullException(nameof(p2p));
        _directLanProbe = directLanProbe ?? throw new ArgumentNullException(nameof(directLanProbe));
        _opts = opts ?? throw new ArgumentNullException(nameof(opts));
        _log = log ?? NullLogger.Instance;
    }

    public event Action<string, ConnectionTier>? TierChanged;

    public async Task<ConnectionResult> ConnectAsync(string targetId, CancellationToken ct = default)
    {
        var started = Stopwatch.GetTimestamp();
        var lastFailure = "unknown";

        if (string.IsNullOrWhiteSpace(targetId))
            return Fail("empty target id", targetId, started);

        // ----- Tier 1: LAN cache lookup -----
        try
        {
            TierChanged?.Invoke(targetId, ConnectionTier.DirectLan);
            var entry = _cache.LookupDevice(targetId);
            if (entry is not null)
            {
                _log.Info($"tier1: cache hit {entry.DeviceId} → {entry.Ip}:{entry.Port}");
                var ok = await _directLanProbe.TryConnectAsync(entry.Ip, entry.Port, ct);
                if (ok)
                {
                    return new ConnectionResult(
                        ConnectionTier.DirectLan, targetId,
                        Ip: entry.Ip, Port: entry.Port,
                        Elapsed: Elapsed(started));
                }
                lastFailure = $"tier1 cache hit but direct connect to {entry.Ip}:{entry.Port} failed";
                _log.Info(lastFailure);
            }
            else
            {
                lastFailure = "tier1: cache miss";
                _log.Info(lastFailure);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return Fail("cancelled", targetId, started);
        }
        catch (Exception ex)
        {
            lastFailure = $"tier1 error: {ex.Message}";
            _log.Info(lastFailure);
        }

        // ----- Tier 2: LAN broadcast / mDNS -----
        try
        {
            TierChanged?.Invoke(targetId, ConnectionTier.LanDiscovered);
            var queryTimeout = TimeSpan.FromSeconds(_opts.LanQueryTimeoutSec);
            var entry = await _discovery.QueryLanAsync(targetId, queryTimeout, ct);
            if (entry is not null)
            {
                _log.Info($"tier2: discovery hit {entry.DeviceId} → {entry.Ip}:{entry.Port}");
                _cache.Upsert(entry, entry.LastSeenMs);
                var ok = await _directLanProbe.TryConnectAsync(entry.Ip, entry.Port, ct);
                if (ok)
                {
                    return new ConnectionResult(
                        ConnectionTier.LanDiscovered, targetId,
                        Ip: entry.Ip, Port: entry.Port,
                        Elapsed: Elapsed(started));
                }
                lastFailure = $"tier2 discovery hit but direct connect to {entry.Ip}:{entry.Port} failed";
                _log.Info(lastFailure);
            }
            else
            {
                lastFailure = "tier2: no LAN response within timeout";
                _log.Info(lastFailure);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return Fail("cancelled", targetId, started);
        }
        catch (Exception ex)
        {
            lastFailure = $"tier2 error: {ex.Message}";
            _log.Info(lastFailure);
        }

        // ----- Tier 3: server lookup + ICE P2P -----
        try
        {
            TierChanged?.Invoke(targetId, ConnectionTier.P2p);
            var lookup = await _serverLookup.LookupAsync(targetId, ct);
            if (lookup is null)
            {
                lastFailure = "tier3: server lookup returned no result (target unknown or offline)";
                _log.Info(lastFailure);
            }
            else if (!lookup.Online)
            {
                lastFailure = $"tier3: target {targetId} registered but offline (lastSeen={lookup.LastSeen:O})";
                _log.Info(lastFailure);
            }
            else
            {
                _log.Info($"tier3: lookup hit {lookup.DeviceId} online ip={lookup.Ip}");
                // Try P2P first (host + srflx candidates gathered internally by the establisher).
                // The establisher receives server-provided ICE servers (STUN).
                var ice = new IceServer[]
                {
                    new(new[] { "stun:stun.l.google.com:19302" }, null, null),
                };
                var p2pOk = await _p2p.TryEstablishAsync(targetId, ice, ct);
                if (p2pOk)
                {
                    return new ConnectionResult(
                        ConnectionTier.P2p, targetId,
                        IceServers: ice,
                        Elapsed: Elapsed(started));
                }
                lastFailure = "tier3: P2P ICE establishment failed";
                _log.Info(lastFailure);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return Fail("cancelled", targetId, started);
        }
        catch (Exception ex)
        {
            lastFailure = $"tier3 error: {ex.Message}";
            _log.Info(lastFailure);
        }

        // ----- Tier 4: TURN relay fallback -----
        try
        {
            TierChanged?.Invoke(targetId, ConnectionTier.Relay);
            var bundle = await _turnClient.GetCredentialsAsync(ct);
            if (bundle is null)
            {
                lastFailure = "tier4: TURN credentials unavailable (TURN not configured?)";
                _log.Info(lastFailure);
            }
            else
            {
                var ice = new IceServer[]
                {
                    new(bundle.Urls, bundle.Username, bundle.Credential),
                };
                var relayOk = await _p2p.TryEstablishAsync(targetId, ice, ct);
                if (relayOk)
                {
                    return new ConnectionResult(
                        ConnectionTier.Relay, targetId,
                        IceServers: ice,
                        TurnUsername: bundle.Username,
                        Elapsed: Elapsed(started));
                }
                lastFailure = "tier4: TURN relay establishment failed";
                _log.Info(lastFailure);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return Fail("cancelled", targetId, started);
        }
        catch (Exception ex)
        {
            lastFailure = $"tier4 error: {ex.Message}";
            _log.Info(lastFailure);
        }

        return Fail(lastFailure, targetId, started);
    }

    private ConnectionResult Fail(string reason, string targetId, long started)
        => new(ConnectionTier.Failed, targetId, FailureReason: reason, Elapsed: Elapsed(started));

    private static TimeSpan Elapsed(long started) =>
        Stopwatch.GetElapsedTime(started);
}

/// <summary>
/// Minimal logging interface. Implementations route to Debug/Trace or the WPF UI.
/// </summary>
public interface ILogger
{
    void Info(string message);
    void Warn(string message);
    void Error(string message, Exception? ex = null);
}

public sealed class NullLogger : ILogger
{
    public static NullLogger Instance { get; } = new();
    public void Info(string message) { }
    public void Warn(string message) { }
    public void Error(string message, Exception? ex = null) { }
}
