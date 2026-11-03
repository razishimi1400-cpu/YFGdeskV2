using System.Security.Cryptography;
using YfgDesk.Shared.Crypto;

namespace YfgDesk.Server.Crypto;

/// <summary>
/// Issues and validates bearer tokens of the form <c>yfg.&lt;64 hex chars&gt;</c>.
/// The token itself is never persisted; only its SHA-256 hex digest is stored.
/// Comparisons use <see cref="Validators.ConstantTimeEquals"/>.
/// </summary>
public sealed class BearerTokenIssuer
{
    public const string Scheme = "Bearer";
    public const string Prefix = "yfg.";
    private const int HexBytes = 32; // 256-bit token = 64 hex chars.

    /// <summary>
    /// Issue a new bearer token + its SHA-256 hex digest.
    /// </summary>
    public (string RawToken, string Digest) Issue()
    {
        Span<byte> buf = stackalloc byte[HexBytes];
        RandomNumberGenerator.Fill(buf);
        var hex = Convert.ToHexString(buf.ToArray()).ToLowerInvariant();
        var raw = Prefix + hex;
        var digest = Hasher.Sha256Hex(raw);
        return (raw, digest);
    }

    /// <summary>
    /// Compute the SHA-256 digest for a previously-issued raw token.
    /// </summary>
    public string DigestOf(string rawToken) => Hasher.Sha256Hex(rawToken);

    /// <summary>
    /// Try to extract a bearer token from an HTTP Authorization header value.
    /// Returns false if the header is missing, has the wrong scheme, or the token is malformed.
    /// </summary>
    public bool TryExtract(string? authorizationHeader, out string rawToken, out string digest)
    {
        rawToken = string.Empty;
        digest = string.Empty;
        if (string.IsNullOrWhiteSpace(authorizationHeader)) return false;
        var span = authorizationHeader.AsSpan().Trim();
        if (!span.StartsWith(Scheme, StringComparison.OrdinalIgnoreCase)) return false;
        span = span.Slice(Scheme.Length).TrimStart();
        if (span.Length == 0) return false;
        var token = span.ToString();
        if (!token.StartsWith(Prefix, StringComparison.Ordinal)) return false;
        var hex = token.AsSpan(Prefix.Length);
        if (hex.Length != 64) return false;
        foreach (var c in hex)
            if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F')))
                return false;
        rawToken = token;
        digest = Hasher.Sha256Hex(token);
        return true;
    }

    /// <summary>
    /// Constant-time digest comparison.
    /// </summary>
    public bool DigestMatches(string storedDigest, string providedDigest) =>
        Validators.ConstantTimeEquals(storedDigest, providedDigest);
}
