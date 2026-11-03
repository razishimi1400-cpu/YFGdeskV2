namespace YfgDesk.Client.Services;

/// <summary>
/// Client-side configuration. Loaded from <c>appsettings.json</c> on Windows
/// and from constructor args on Linux/tests. Defaults are tuned for v2.0.
/// </summary>
public sealed class ClientOptions
{
    /// <summary>
    /// Base URL of the central server (no trailing slash). e.g. <c>https://yfgdesk.example.com</c>.
    /// </summary>
    public string ServerBaseUrl { get; set; } = "http://localhost:5080";

    /// <summary>
    /// WebSocket URL for signaling. Defaults to <c>wss://&lt;ServerBaseUrl host&gt;/ws</c>
    /// if null. Computed in <see cref="ResolveWebSocketUrl"/>.
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
    /// Cache lifetime for a LAN discovery entry (seconds). Entries older than
    /// this are treated as stale and won't be used by Tier 1.
    /// </summary>
    public int LanCacheTtlSec { get; set; } = 60;

    /// <summary>
    /// WebSocket heartbeat interval (seconds).
    /// </summary>
    public int HeartbeatIntervalSec { get; set; } = 30;

    /// <summary>
    /// Signaling reconnect backoff (seconds). Doubles up to <see cref="ReconnectMaxBackoffSec"/>.
    /// </summary>
    public int ReconnectInitialBackoffSec { get; set; } = 1;
    public int ReconnectMaxBackoffSec { get; set; } = 30;

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
    /// explicitly set, use it; otherwise derive from <see cref="ServerBaseUrl"/>.
    /// </summary>
    public string ResolveWebSocketUrl()
    {
        if (!string.IsNullOrWhiteSpace(WebSocketUrl)) return WebSocketUrl!;
        var baseUri = new Uri(ServerBaseUrl.TrimEnd('/'));
        var scheme = baseUri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase) ? "wss" : "ws";
        return $"{scheme}://{baseUri.Host}:{baseUri.Port}/ws";
    }
}
