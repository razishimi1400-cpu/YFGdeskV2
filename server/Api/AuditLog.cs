using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using YfgDesk.Server.Data;

namespace YfgDesk.Server.Api;

/// <summary>
/// Writes audit rows into the <see cref="AuditEventEntity"/> table. The Detail field
/// is always sanitized to:
///   - never contain bearer tokens, nonces, signatures, SDP, or passwords
///   - be at most 256 characters after truncation
/// Failures to write audit records MUST NOT throw — audit is best-effort.
/// </summary>
public sealed class AuditLog
{
    private static readonly string[] SecretFragments =
    {
        "yfg.", "bearer", "nonce", "signature", "sdp", "password", "secret", "credential",
        "a=fingerprint", "candidate:", "hmac",
    };

    private readonly IServiceProvider _services;
    private readonly ILogger<AuditLog> _logger;

    public AuditLog(IServiceProvider services, ILogger<AuditLog> logger)
    {
        _services = services;
        _logger = logger;
    }

    /// <summary>
    /// Synchronously record an audit event. Best-effort: failures are swallowed.
    /// </summary>
    public void Record(string kind, string? deviceId = null, string? sessionId = null,
        string? ipHint = null, string? detail = null)
    {
        var sanitizedDetail = SanitizeDetail(detail);
        var entity = new AuditEventEntity
        {
            CreatedAt = DateTime.UtcNow,
            Kind = Truncate(kind, 64),
            DeviceId = TruncateOrNull(deviceId, 9),
            SessionId = TruncateOrNull(sessionId, 40),
            IpHint = TruncateOrNull(ipHint, 64),
            Detail = TruncateOrNull(sanitizedDetail, 256),
        };
        try
        {
            using var scope = _services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<YfgDeskDbContext>();
            db.AuditEvents.Add(entity);
            db.SaveChanges();
        }
        catch (Exception ex)
        {
            // Audit failures are non-fatal; log to console but never throw.
            _logger.LogWarning(ex, "Failed to write audit event kind={Kind}", kind);
        }
    }

    /// <summary>
    /// Asynchronously record an audit event.
    /// </summary>
    public async Task RecordAsync(string kind, string? deviceId, string? sessionId,
        string? ipHint, string? detail, CancellationToken ct = default)
    {
        var sanitizedDetail = SanitizeDetail(detail);
        var entity = new AuditEventEntity
        {
            CreatedAt = DateTime.UtcNow,
            Kind = Truncate(kind, 64),
            DeviceId = TruncateOrNull(deviceId, 9),
            SessionId = TruncateOrNull(sessionId, 40),
            IpHint = TruncateOrNull(ipHint, 64),
            Detail = TruncateOrNull(sanitizedDetail, 256),
        };
        try
        {
            using var scope = _services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<YfgDeskDbContext>();
            db.AuditEvents.Add(entity);
            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to write audit event kind={Kind}", kind);
        }
    }

    /// <summary>
    /// Sanitize a detail string by removing any embedded secrets and truncating to 256 chars.
    /// </summary>
    public static string? SanitizeDetail(string? detail)
    {
        if (string.IsNullOrEmpty(detail)) return null;
        var s = detail;
        foreach (var frag in SecretFragments)
        {
            if (s.Contains(frag, StringComparison.OrdinalIgnoreCase))
            {
                // Replace any line containing the fragment with a placeholder.
                s = StripFragment(s, frag);
            }
        }
        s = s.Replace('\r', ' ').Replace('\n', ' ').Trim();
        if (s.Length > 256) s = s.Substring(0, 256);
        return string.IsNullOrWhiteSpace(s) ? null : s;
    }

    private static string StripFragment(string s, string fragment)
    {
        // Replace the fragment occurrence and a small tail (to avoid leaking partial secrets).
        var sb = new System.Text.StringBuilder(s.Length);
        var i = 0;
        while (i < s.Length)
        {
            var end = System.Math.Min(i + fragment.Length, s.Length);
            var slice = s.AsSpan(i, end - i);
            if (slice.Equals(fragment.AsSpan(), StringComparison.OrdinalIgnoreCase))
            {
                sb.Append("[redacted]");
                i = end;
                // Skip the following token up to whitespace.
                while (i < s.Length && !char.IsWhiteSpace(s[i])) i++;
            }
            else
            {
                sb.Append(s[i]);
                i++;
            }
        }
        return sb.ToString();
    }

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s.Substring(0, max);

    private static string? TruncateOrNull(string? s, int max)
    {
        if (string.IsNullOrEmpty(s)) return null;
        return s.Length <= max ? s : s.Substring(0, max);
    }
}
