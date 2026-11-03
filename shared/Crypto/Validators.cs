using System.Text.RegularExpressions;

namespace YfgDesk.Shared.Crypto;

/// <summary>
/// Pure (no WebCrypto / no System.Security.Cryptography) validators shared by
/// both server and client. These are intentionally side-effect-free and run
/// on both .NET 10 (server) and .NET 10-windows (client) targets.
/// </summary>
public static class Validators
{
    /// <summary>
    /// A device ID is exactly 9 digits (like AnyDesk). Must be all-numeric.
    /// Example: "123456789".
    /// </summary>
    public static bool IsValidDeviceId(string? id)
    {
        if (string.IsNullOrEmpty(id)) return false;
        return id.Length == 9 && id.All(char.IsDigit);
    }

    /// <summary>
    /// A session ID has the prefix "s_" + 22 Crockford base32 chars.
    /// Example: "s_ABCDEFGHJKMNPQRSTVWXY0".
    /// </summary>
    public static bool IsValidSessionId(string? id)
    {
        if (string.IsNullOrEmpty(id)) return false;
        if (!id.StartsWith("s_")) return false;
        var body = id.Substring(2);
        if (body.Length is < 16 or > 32) return false;
        return body.All(c => "0123456789ABCDEFGHJKMNPQRSTVWXYZ".Contains(char.ToUpperInvariant(c)));
    }

    /// <summary>
    /// Validate a STUN/TURN URL. Grammar: scheme:host[:port][?transport=...].
    /// Scheme must be stun/stuns/turn/turns.
    /// </summary>
    public static bool IsValidIceUrl(string? url)
    {
        if (string.IsNullOrEmpty(url)) return false;
        if (url.Length > 512) return false;
        return Regex.IsMatch(url, @"^(stun|stuns|turn|turns):[^\s@]{1,500}$", RegexOptions.IgnoreCase);
    }

    /// <summary>
    /// Validate an SDP. Must:
    /// - be at most 12 KiB UTF-8
    /// - contain a SHA-256 fingerprint
    /// - contain a video media line
    /// </summary>
    public static bool IsValidSdp(string? sdp, int maxBytes = 12 * 1024)
    {
        if (string.IsNullOrEmpty(sdp)) return false;
        if (System.Text.Encoding.UTF8.GetByteCount(sdp) > maxBytes) return false;
        if (!sdp.Contains("a=fingerprint:sha-256 ")) return false;
        if (!sdp.Contains("m=video")) return false;
        // Reject CR-only line endings; require LF or CRLF.
        if (Regex.IsMatch(sdp, @"\r[^\n]")) return false;
        return true;
    }

    /// <summary>
    /// Validate an ICE candidate. Must:
    /// - be at most 4 KiB UTF-8
    /// - match the candidate grammar: candidate:&lt;foundation&gt; &lt;component&gt; &lt;proto&gt; &lt;prio&gt; &lt;addr&gt; &lt;port&gt; typ &lt;type&gt;
    /// </summary>
    public static bool IsValidIceCandidate(string? candidate, int maxBytes = 4 * 1024)
    {
        if (string.IsNullOrEmpty(candidate)) return false;
        if (System.Text.Encoding.UTF8.GetByteCount(candidate) > maxBytes) return false;
        return Regex.IsMatch(candidate,
            @"^candidate:\S+ \d+ (udp|tcp) \d+ \S+ \d+ typ (host|srflx|prflx|relay)( raddr \S+ rport \d+)?",
            RegexOptions.IgnoreCase);
    }

    /// <summary>
    /// Validate a display name. 1-64 chars after sanitizing.
    /// </summary>
    public static bool IsValidDisplayName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        var sanitized = SanitizeDisplayName(name);
        return sanitized.Length is >= 1 and <= 64;
    }

    /// <summary>
    /// Strip control characters and collapse whitespace in a display name.
    /// </summary>
    public static string SanitizeDisplayName(string? name)
    {
        if (string.IsNullOrEmpty(name)) return string.Empty;
        // Strip C0/C1 control chars.
        var sb = new System.Text.StringBuilder(name.Length);
        foreach (var c in name)
        {
            if (!char.IsControl(c)) sb.Append(c);
        }
        // Collapse runs of whitespace to a single space.
        return Regex.Replace(sb.ToString(), @"\s+", " ").Trim();
    }

    /// <summary>
    /// Validate a video preference (width/height/fps bounds).
    /// </summary>
    public static bool IsValidVideoPreference(int width, int height, int fps, string? qualityMode)
    {
        if (width is < 320 or > 3840) return false;
        if (height is < 240 or > 2160) return false;
        if (fps is < 1 or > 60) return false;
        if (qualityMode is null) return false;
        return qualityMode is "Small" or "Standard" or "Custom";
    }

    /// <summary>
    /// Constant-time string compare. Used for bearer token hash comparison.
    /// </summary>
    public static bool ConstantTimeEquals(string a, string b)
    {
        if (a.Length != b.Length) return false;
        var diff = 0;
        for (var i = 0; i < a.Length; i++) diff |= a[i] ^ b[i];
        return diff == 0;
    }
}
