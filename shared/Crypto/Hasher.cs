using System.Security.Cryptography;

namespace YfgDesk.Shared.Crypto;

/// <summary>
/// SHA-256 helpers used for hashing bearer tokens and public keys.
/// </summary>
public static class Hasher
{
    /// <summary>
    /// Compute the SHA-256 hex digest of a UTF-8 string.
    /// </summary>
    public static string Sha256Hex(string input)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(input);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>
    /// Compute the SHA-256 hex digest of raw bytes.
    /// </summary>
    public static string Sha256Hex(byte[] input)
    {
        var hash = SHA256.HashData(input);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
