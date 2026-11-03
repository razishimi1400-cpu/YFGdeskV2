using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using YfgDesk.Shared.Protocol;

namespace YfgDesk.Server.Tests;

/// <summary>
/// Helpers for generating ECDSA P-256 keypairs, signing nonces, and building
/// JSON envelopes for the test harness.
/// </summary>
internal static class CryptoTestUtils
{
    public static (string SpkiBase64, ECDsa Key) GenerateP256KeyPair()
    {
        var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var spki = key.ExportSubjectPublicKeyInfo();
        return (Convert.ToBase64String(spki), key);
    }

    public static string SignNonce(ECDsa key, string nonce)
    {
        var sig = key.SignData(Encoding.UTF8.GetBytes(nonce), HashAlgorithmName.SHA256);
        return Convert.ToBase64String(sig);
    }

    public static string BuildEnvelope(string kind, string messageId, string? sessionId,
        object payload, long? timestampOverride = null)
    {
        var env = new
        {
            version = ProtocolVersion.Current,
            messageId,
            sessionId,
            timestamp = timestampOverride ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            kind,
            payload,
        };
        return JsonSerializer.Serialize(env, JsonOptions.Default);
    }
}
