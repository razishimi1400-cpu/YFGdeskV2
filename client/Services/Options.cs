namespace YfgDesk.Client.Services;

/// <summary>
/// Client-side configuration. Loaded from <c>appsettings.json</c> on Windows
/// and from constructor args on Linux/tests. Defaults are tuned for v2.0.
/// </summary>
public sealed class ClientOptions
{
    /// <summary>
    /// Base URL of the central server (no trailing slash). e.g. <c>https://yfgdesk.example.com</c>.
    ///
    /// If left null (default), the client uses the embedded obfuscated server
    /// list from <see cref="ServerEndpoints"/>. The embedded list contains
    /// three production bootstrap servers with automatic failover.
    ///
    /// Set this only for development/testing against a local server.
    /// </summary>
    public string? ServerBaseUrl { get; set; }

    /// <summary>
    /// The currently-active server base URL. Selected from
    /// <see cref="GetServerCandidates"/> on first use, and rotated on
    /// connection failure. Read-only after construction — use
    /// <see cref="AdvanceToNextServer"/> to rotate.
    /// </summary>
    private string? _activeServerBaseUrl;

    /// <summary>
    /// Returns the ordered list of server candidates. If <see cref="ServerBaseUrl"/>
    /// is explicitly set, only that one is returned. Otherwise, the embedded
    /// obfuscated list is returned (in stable insertion order).
    /// </summary>
    public IReadOnlyList<string> GetServerCandidates()
    {
        if (!string.IsNullOrWhiteSpace(ServerBaseUrl))
        {
            return new[] { ServerBaseUrl!.TrimEnd('/') };
        }
        return ServerEndpoints.GetAll();
    }

    /// <summary>
    /// Get the currently-active server base URL. On first call, picks the
    /// first candidate. Subsequent calls return the same one until
    /// <see cref="AdvanceToNextServer"/> is called.
    /// </summary>
    public string GetActiveServerBaseUrl()
    {
        if (!string.IsNullOrWhiteSpace(_activeServerBaseUrl))
            return _activeServerBaseUrl!;

        var candidates = GetServerCandidates();
        if (candidates.Count == 0)
            throw new InvalidOperationException("No server endpoints configured.");

        _activeServerBaseUrl = candidates[0];
        return _activeServerBaseUrl;
    }

    /// <summary>
    /// Rotate to the next server in the candidate list. Returns false if we
    /// have cycled through all of them (caller should give up or back off).
    /// </summary>
    public bool AdvanceToNextServer()
    {
        var candidates = GetServerCandidates();
        if (candidates.Count <= 1) return false;

        var current = _activeServerBaseUrl;
        int idx = -1;
        for (var i = 0; i < candidates.Count; i++)
        {
            if (candidates[i] == current) { idx = i; break; }
        }
        var nextIdx = (idx + 1) % candidates.Count;
        _activeServerBaseUrl = candidates[nextIdx];
        return nextIdx != 0;  // false when we've wrapped around to the start
    }

    /// <summary>
    /// Reset to the first server. Called after a successful reconnect to
    /// a primary server.
    /// </summary>
    public void ResetToPrimaryServer()
    {
        _activeServerBaseUrl = null;
    }

    /// <summary>
    /// WebSocket URL for signaling. If set explicitly, used as-is. Otherwise
    /// derived from the active server base URL.
    /// </summary>
    public string? WebSocketUrl { get; set; }

    /// <summary>
    /// UDP port used for LAN broadcast discovery (broadcast source + sink).
    /// </summary>
    public int LanDiscoveryPort { get; set; } = 49152;

    /// <summary>
    /// TCP port that the host listens on for direct LAN connections (Tier 1).
    /// </summary>
    public int LanListenPort { get; set; } = 49153;

    /// <summary>
    /// mDNS service type. Null disables mDNS.
    /// </summary>
    public string? MdnsServiceType { get; set; } = "_yfgdesk._tcp.local";

    /// <summary>
    /// Interval between outgoing LAN broadcasts (seconds).
    /// </summary>
    public int LanBroadcastIntervalSec { get; set; } = 5;

    /// <summary>
    /// LAN discovery query timeout (seconds) for Tier 2 of the orchestrator.
    /// </summary>
    public int LanQueryTimeoutSec { get; set; } = 3;

    /// <summary>
    /// Cache lifetime for a LAN discovery entry (seconds).
    /// </summary>
    public int LanCacheTtlSec { get; set; } = 60;

    /// <summary>
    /// WebSocket heartbeat interval (seconds).
    /// </summary>
    public int HeartbeatIntervalSec { get; set; } = 30;

    /// <summary>
    /// Signaling reconnect backoff (seconds).
    /// </summary>
    public int ReconnectInitialBackoffSec { get; set; } = 1;
    public int ReconnectMaxBackoffSec { get; set; } = 30;

    /// <summary>
    /// Number of times to try reconnecting to the current server before
    /// advancing to the next server in the candidate list.
    /// </summary>
    public int ReconnectAttemptsPerServer { get; set; } = 3;

    /// <summary>
    /// Default video preference sent by the viewer in <c>createSession</c>.
    /// </summary>
    public int DefaultVideoWidth { get; set; } = 1920;
    public int DefaultVideoHeight { get; set; } = 1080;
    public int DefaultVideoFps { get; set; } = 30;
    public string DefaultVideoQualityMode { get; set; } = "Standard";

    /// <summary>
    /// Host-consent request expiry (seconds). Server caps at 300.
    /// </summary>
    public int ConsentTimeoutSec { get; set; } = 300;

    /// <summary>
    /// Max chunk size for file-transfer DataChannel messages (bytes).
    /// </summary>
    public int FileTransferChunkBytes { get; set; } = 16 * 1024;

    /// <summary>
    /// Local display name advertised on the LAN. Defaults to the machine name.
    /// </summary>
    public string DisplayName { get; set; } = Environment.MachineName;

    /// <summary>
    /// Resolve the signaling WebSocket URL. If <see cref="WebSocketUrl"/> is
    /// explicitly set, use it; otherwise derive from the active server.
    /// </summary>
    public string ResolveWebSocketUrl()
    {
        if (!string.IsNullOrWhiteSpace(WebSocketUrl)) return WebSocketUrl!;
        var baseUri = new Uri(GetActiveServerBaseUrl().TrimEnd('/'));
        var scheme = baseUri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase) ? "wss" : "ws";
        return $"{scheme}://{baseUri.Host}:{baseUri.Port}/ws";
    }
}
