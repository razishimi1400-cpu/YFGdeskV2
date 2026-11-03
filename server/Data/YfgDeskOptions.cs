namespace YfgDesk.Server.Data;

/// <summary>
/// Strongly-typed configuration options for the YFGDESK server.
/// Bound from appsettings.json + environment variables.
/// </summary>
public sealed class YfgDeskOptions
{
    public const string SectionName = "YfgDesk";

    /// <summary>
    /// SQLite connection string. Default uses env var YFGDESK_DB if present.
    /// </summary>
    public string DatabaseConnectionString { get; set; } =
        Environment.GetEnvironmentVariable("YFGDESK_DB") ?? "Data Source=yfgdesk.db";

    /// <summary>
    /// Maximum allowed request body size in bytes (global). Default 32 KiB.
    /// </summary>
    public int MaxRequestBodyBytes { get; set; } = 32 * 1024;

    /// <summary>
    /// Per-endpoint body size cap. Default 16 KiB.
    /// </summary>
    public int PerEndpointMaxBodyBytes { get; set; } = 16 * 1024;

    /// <summary>
    /// Challenge TTL (seconds). Default 60s.
    /// </summary>
    public int ChallengeTtlSeconds { get; set; } = 60;

    /// <summary>
    /// Maximum number of pending challenges in memory. Default 4096.
    /// </summary>
    public int MaxPendingChallenges { get; set; } = 4096;

    /// <summary>
    /// Bearer token TTL (seconds). Default 7 days.
    /// </summary>
    public int BearerTtlSeconds { get; set; } = 7 * 24 * 3600;

    /// <summary>
    /// Per-IP enroll rate limit (requests per minute).
    /// </summary>
    public int EnrollIpRateLimitPerMinute { get; set; } = 60;

    /// <summary>
    /// Per-device WebSocket rate limit (messages per minute).
    /// </summary>
    public int WsDeviceRateLimitPerMinute { get; set; } = 60;

    /// <summary>
    /// PENDING → consent wait timeout (seconds). Default 300s (5 min).
    /// </summary>
    public int ConsentWaitSeconds { get; set; } = 300;

    /// <summary>
    /// Session idle timeout (seconds). Default 120s (2 min).
    /// </summary>
    public int SessionIdleSeconds { get; set; } = 120;

    /// <summary>
    /// Hard maximum session duration (seconds). Default 43200 (12 h).
    /// </summary>
    public int SessionMaxLifetimeSeconds { get; set; } = 12 * 3600;

    /// <summary>
    /// Periodic sweep interval (seconds). Default 30s.
    /// </summary>
    public int SweepIntervalSeconds { get; set; } = 30;

    /// <summary>
    /// WebSocket keep-alive interval (seconds). Default 15s.
    /// </summary>
    public int WsKeepAliveSeconds { get; set; } = 15;

    /// <summary>
    /// Heartbeat interval advertised to clients (seconds). Default 30s.
    /// </summary>
    public int HeartbeatIntervalSeconds { get; set; } = 30;

    /// <summary>
    /// Anti-replay window size (number of remembered messageIds per session).
    /// </summary>
    public int ReplayWindowSize { get; set; } = 512;

    /// <summary>
    /// Allowed timestamp skew in seconds. ±120s default.
    /// </summary>
    public int TimestampSkewSeconds { get; set; } = 120;

    /// <summary>
    /// TURN configuration block (read from YFGDESK_TURN_* env vars at startup).
    /// </summary>
    public TurnOptions Turn { get; set; } = new();
}

public sealed class TurnOptions
{
    /// <summary>
    /// Comma-separated list of TURN/STUN URLs, e.g.
    /// "turn:turn.example.com:3478?transport=udp,turn:turn.example.com:3478?transport=tcp".
    /// Read from YFGDESK_TURN_URLS.
    /// </summary>
    public string[] Urls { get; set; } = Array.Empty<string>();

    /// <summary>
    /// Shared secret for HMAC-SHA1 credential derivation. Read from YFGDESK_TURN_SHARED_SECRET.
    /// </summary>
    public string SharedSecret { get; set; } = string.Empty;

    /// <summary>
    /// TURN realm (informational). Read from YFGDESK_TURN_REALM.
    /// </summary>
    public string Realm { get; set; } = string.Empty;

    /// <summary>
    /// TURN credential TTL (seconds). Default 7200 (2 hours).
    /// </summary>
    public int TtlSeconds { get; set; } = 2 * 3600;

    /// <summary>
    /// True when both URLs and shared secret are non-empty.
    /// </summary>
    public bool IsConfigured => Urls.Length > 0 && !string.IsNullOrEmpty(SharedSecret);
}
