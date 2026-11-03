using System.Security.Cryptography;

namespace YfgDesk.Server.Crypto;

/// <summary>
/// Imports a P-256 SPKI (SubjectPublicKeyInfo) base64 public key and verifies
/// DER-encoded (RFC 3279) ECDSA signatures over SHA-256 of UTF-8 messages.
/// </summary>
public sealed class EcdsaVerifier : IDisposable
{
    private ECDsa? _key;

    /// <summary>
    /// Import a P-256 SPKI base64 string. Returns false if the key is malformed
    /// or uses a curve other than P-256 / secp256r1 / prime256v1.
    /// </summary>
    public bool TryImportSpkiBase64(string base64Spki)
    {
        if (string.IsNullOrWhiteSpace(base64Spki)) return false;
        if (base64Spki.Length > 4096) return false;

        byte[] spkiBytes;
        try
        {
            spkiBytes = Convert.FromBase64String(base64Spki.Trim());
        }
        catch
        {
            return false;
        }
        if (spkiBytes.Length is 0 or > 1024) return false;

        try
        {
            var key = ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(spkiBytes, out _);
            // Verify curve is P-256.
            var ecParams = key.ExportParameters(false);
            if (!ECParametersAreP256(ecParams))
            {
                key.Dispose();
                return false;
            }
            _key?.Dispose();
            _key = key;
            return true;
        }
        catch (CryptographicException)
        {
            return false;
        }
    }

    private static bool ECParametersAreP256(ECParameters p)
    {
        // P-256 OID: 1.2.840.10045.3.1.7 (also known as prime256v1 / secp256r1)
        var oid = p.Curve.Oid?.Value;
        return oid == "1.2.840.10045.3.1.7";
    }

    /// <summary>
    /// Verify a DER-encoded ECDSA signature over SHA-256 of the UTF-8 bytes of <paramref name="message"/>.
    /// </summary>
    public bool VerifySignature(string message, string base64DerSignature)
    {
        if (_key is null) return false;
        if (string.IsNullOrEmpty(message) || string.IsNullOrEmpty(base64DerSignature)) return false;
        if (base64DerSignature.Length > 1024) return false;

        byte[] sig;
        try { sig = Convert.FromBase64String(base64DerSignature.Trim()); }
        catch { return false; }
        if (sig.Length is 0 or > 264) return false; // DER P-256 sigs are 70-72 bytes typically.

        var msgBytes = System.Text.Encoding.UTF8.GetBytes(message);
        try
        {
            return _key.VerifyData(msgBytes, sig, HashAlgorithmName.SHA256);
        }
        catch (CryptographicException)
        {
            return false;
        }
    }

    /// <summary>
    /// Verify a DER-encoded ECDSA signature over SHA-256 of the given raw bytes.
    /// </summary>
    public bool VerifySignatureBytes(byte[] messageBytes, byte[] derSignature)
    {
        if (_key is null) return false;
        if (messageBytes is null || messageBytes.Length == 0 || messageBytes.Length > 4096) return false;
        if (derSignature is null || derSignature.Length is 0 or > 264) return false;
        try { return _key.VerifyData(messageBytes, derSignature, HashAlgorithmName.SHA256); }
        catch (CryptographicException) { return false; }
    }

    public void Dispose() => _key?.Dispose();
}
