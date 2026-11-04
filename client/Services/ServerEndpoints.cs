using System.Security.Cryptography;
using System.Text;

namespace YfgDesk.Client.Services;

/// <summary>
/// Obfuscated list of bootstrap server endpoints.
///
/// The addresses are NOT stored in plaintext in the binary or in
/// appsettings.json. Instead, they are XOR-encoded with a fixed key and
/// Base64-encoded. This defeats casual `strings`/grep inspection of the
/// compiled binary — a determined attacker can still extract them by
/// running the decoder, but it raises the bar above "open the .exe in a
/// hex editor".
///
/// For a production deployment where the server list is genuinely secret,
/// use proper authenticated encryption (AES-GCM with a key derived from a
/// machine-bound secret via DPAPI). This obfuscation is sufficient for
/// hiding the list from casual inspection.
/// </summary>
public static class ServerEndpoints
{
    /// <summary>
    /// XOR key — 32 random bytes. Compiled into the binary.
    /// Changing this key INVALIDATES all existing encoded blobs.
    /// </summary>
    private static readonly byte[] ObfuscationKey = new byte[]
    {
        0x9c, 0x47, 0xa1, 0x3e, 0x08, 0xd2, 0x7f, 0xb6,
        0xc5, 0x11, 0x4a, 0x90, 0xf3, 0x2e, 0x6d, 0x88,
        0x01, 0xb9, 0x54, 0xc0, 0x76, 0x2a, 0xef, 0x3d,
        0x4c, 0x97, 0xe2, 0x18, 0xd5, 0x6b, 0xf0, 0xa4,
    };

    /// <summary>
    /// Encoded server endpoints. Each entry is Base64(XOR(utf8 bytes, key)).
    ///
    /// To regenerate, run:
    ///   ServerEndpoints.Encode("http://188.121.147.94:7070")
    /// and paste the result here.
    /// </summary>
    private static readonly string[] EncodedEndpoints = new[]
    {
        "9DPVTjL9UIf9KWShwR9DuTWOevlCENgNe6c=",
        "9DPVTjL9UIXyP3uhxwBfuTeXZvJCENgNe6c=",
        "9DPVTjL9UI/xP3uowQBeuS+IYPlMHd8KfA==",
    };

    /// <summary>
    /// Decode all server endpoints in random order. Callers should try
    /// each one in order and use the first that responds.
    /// </summary>
    public static IReadOnlyList<string> GetAll()
    {
        var list = new List<string>(EncodedEndpoints.Length);
        foreach (var encoded in EncodedEndpoints)
        {
            try
            {
                var decoded = Decode(encoded);
                if (!string.IsNullOrEmpty(decoded)) list.Add(decoded);
            }
            catch
            {
                // Skip malformed entry — fail closed rather than crash.
            }
        }
        // Stable order: keep insertion order (server 1 first).
        return list;
    }

    /// <summary>
    /// Encode a URL for embedding in <see cref="EncodedEndpoints"/>.
    /// Used only at build time to regenerate the blob.
    /// </summary>
    public static string Encode(string plaintext)
    {
        var bytes = Encoding.UTF8.GetBytes(plaintext);
        var xored = new byte[bytes.Length];
        for (var i = 0; i < bytes.Length; i++)
        {
            xored[i] = (byte)(bytes[i] ^ ObfuscationKey[i % ObfuscationKey.Length]);
        }
        return Convert.ToBase64String(xored);
    }

    /// <summary>
    /// Decode an obfuscated blob back to the original URL.
    /// </summary>
    public static string Decode(string encoded)
    {
        var xored = Convert.FromBase64String(encoded);
        var bytes = new byte[xored.Length];
        for (var i = 0; i < xored.Length; i++)
        {
            bytes[i] = (byte)(xored[i] ^ ObfuscationKey[i % ObfuscationKey.Length]);
        }
        return Encoding.UTF8.GetString(bytes);
    }

    /// <summary>
    /// Shuffle the endpoints deterministically by a seed (e.g. device ID hash).
    /// This makes it harder to predict which server a given client will hit
    /// first, distributing load across all three.
    /// </summary>
    public static IReadOnlyList<string> GetAllShuffled(string? seed = null)
    {
        var list = GetAll().ToList();
        if (list.Count <= 1) return list;

        // Deterministic shuffle by seed (e.g. deviceId hash). If no seed,
        // use a per-process random (still stable for the process lifetime).
        var rng = seed is null
            ? new Random(unchecked((int)DateTime.UtcNow.Ticks))
            : new Random(GetStableHash(seed));

        // Fisher-Yates
        for (var i = list.Count - 1; i > 0; i--)
        {
            var j = rng.Next(i + 1);
            (list[i], list[j]) = (list[j], list[i]);
        }
        return list;
    }

    private static int GetStableHash(string s)
    {
        unchecked
        {
            int hash = 17;
            foreach (var c in s) hash = hash * 31 + c;
            return hash;
        }
    }
}
