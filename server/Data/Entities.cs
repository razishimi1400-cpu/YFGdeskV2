using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using YfgDesk.Shared.Protocol;

namespace YfgDesk.Server.Data;

/// <summary>
/// A registered device. The primary key is the 9-digit numeric DeviceId.
/// </summary>
public sealed class DeviceEntity
{
    [Key]
    [StringLength(9)]
    public string DeviceId { get; set; } = string.Empty;

    /// <summary>
    /// P-256 SPKI public key, base64-encoded.
    /// </summary>
    [Required]
    public string PublicKeySpki { get; set; } = string.Empty;

    /// <summary>
    /// SHA-256 hex digest of the SPKI bytes. Used for lookups/audit.
    /// </summary>
    [Required]
    [StringLength(64)]
    public string PublicKeyDigest { get; set; } = string.Empty;

    /// <summary>
    /// Bearer token SHA-256 hex digest (we never store plaintext bearer tokens).
    /// </summary>
    [StringLength(64)]
    public string? BearerDigest { get; set; }

    /// <summary>
    /// Bearer token expiry (UTC).
    /// </summary>
    public DateTime? BearerExpiresAt { get; set; }

    /// <summary>
    /// Last known role of this device.
    /// </summary>
    [StringLength(16)]
    public string Role { get; set; } = "VIEWER";

    /// <summary>
    /// Last known public IPv4 as observed by the server.
    /// </summary>
    [StringLength(64)]
    public string? LastSeenIp { get; set; }

    /// <summary>
    /// Comma-separated list of TCP/UDP listening ports reported by the device.
    /// </summary>
    [StringLength(256)]
    public string? LastSeenPorts { get; set; }

    /// <summary>
    /// All public IPs observed by the server (CSV). Used for STUN candidates / audit.
    /// </summary>
    [StringLength(512)]
    public string? PublicIps { get; set; }

    /// <summary>
    /// True when the device has sent a heartbeat within the past 90 seconds.
    /// </summary>
    public bool Online { get; set; }

    /// <summary>
    /// Last heartbeat timestamp (UTC). Null if never seen.
    /// </summary>
    public DateTime? LastHeartbeatAt { get; set; }

    /// <summary>
    /// Sanitized display name reported by the device.
    /// </summary>
    [StringLength(64)]
    public string? DisplayName { get; set; }

    /// <summary>
    /// Creation timestamp (UTC).
    /// </summary>
    public DateTime CreatedAt { get; set; }

    [NotMapped]
    public string[] ListenPortsList =>
        string.IsNullOrEmpty(LastSeenPorts)
            ? Array.Empty<string>()
            : LastSeenPorts.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    [NotMapped]
    public string[] PublicIpsList =>
        string.IsNullOrEmpty(PublicIps)
            ? Array.Empty<string>()
            : PublicIps.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}

/// <summary>
/// A signaling session between a Host and a Viewer.
/// </summary>
public sealed class SessionEntity
{
    [Key]
    [StringLength(40)]
    public string SessionId { get; set; } = string.Empty;

    [Required]
    [StringLength(9)]
    public string HostDeviceId { get; set; } = string.Empty;

    [Required]
    [StringLength(9)]
    public string ViewerDeviceId { get; set; } = string.Empty;

    /// <summary>
    /// Stored as the underlying int of <see cref="SessionState"/>. Use the mapped property for typed access.
    /// </summary>
    public int StateRaw { get; set; } = (int)SessionState.Pending;

    [NotMapped]
    public SessionState State
    {
        get => (SessionState)StateRaw;
        set => StateRaw = (int)value;
    }

    public int RequestedPermissions { get; set; }
    public int? GrantedPermissions { get; set; }

    public int VideoWidth { get; set; }
    public int VideoHeight { get; set; }
    public int VideoFramesPerSecond { get; set; }
    [StringLength(16)]
    public string VideoQualityMode { get; set; } = "Standard";

    /// <summary>
    /// Sanitized display name of the viewer reported at createSession time.
    /// </summary>
    [StringLength(64)]
    public string ViewerDisplayName { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; }
    public DateTime? AcceptedAt { get; set; }
    public DateTime? LastHeartbeatAt { get; set; }
    public DateTime ExpiresAt { get; set; }
    public DateTime? EndedAt { get; set; }

    [StringLength(64)]
    public string? EndReason { get; set; }
}

/// <summary>
/// A security-relevant audit event. Detail is capped at 256 chars and never
/// contains bearer/nonce/signature/SDP/password material.
/// </summary>
public sealed class AuditEventEntity
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public long Id { get; set; }

    public DateTime CreatedAt { get; set; }

    [Required]
    [StringLength(64)]
    public string Kind { get; set; } = string.Empty;

    [StringLength(9)]
    public string? DeviceId { get; set; }

    [StringLength(40)]
    public string? SessionId { get; set; }

    [StringLength(64)]
    public string? IpHint { get; set; }

    [StringLength(256)]
    public string? Detail { get; set; }
}
