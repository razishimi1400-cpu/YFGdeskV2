using System.Security.Cryptography;
using YfgDesk.Shared.Crypto;

namespace YfgDesk.Server.Crypto;

/// <summary>
/// Generates a session ID of the form "s_" + 22 Crockford base32 chars
/// derived from 13 cryptographically-random bytes.
/// </summary>
public sealed class SessionIdGenerator
{
    private const int RandomBytes = 13;
    private const int CrockfordLength = 22; // 13 bytes * 8 bits = 104 bits → ceil(104/5) = 21 chars; pad to 22.

    public string Generate()
    {
        Span<byte> buf = stackalloc byte[RandomBytes];
        RandomNumberGenerator.Fill(buf);
        var encoded = CrockfordBase32.Encode(buf.ToArray());
        // Encoded length is 21 chars (105 bits rounded up). Pad with one extra random char to reach 22.
        if (encoded.Length < CrockfordLength)
        {
            Span<byte> pad = stackalloc byte[1];
            RandomNumberGenerator.Fill(pad);
            var padChar = "0123456789ABCDEFGHJKMNPQRSTVWXYZ"[pad[0] % 32];
            return "s_" + encoded + padChar;
        }
        return "s_" + encoded.Substring(0, CrockfordLength);
    }
}
