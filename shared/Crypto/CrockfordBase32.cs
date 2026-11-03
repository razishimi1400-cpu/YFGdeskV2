namespace YfgDesk.Shared.Crypto;

/// <summary>
/// Crockford base32 (no I/L/O/U) used for device IDs and session IDs.
/// Used for short, human-readable, unambiguous IDs.
/// </summary>
public static class CrockfordBase32
{
    private const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

    /// <summary>
    /// Encode bytes as Crockford base32 (no padding, uppercase).
    /// </summary>
    public static string Encode(byte[] bytes)
    {
        if (bytes.Length == 0) return string.Empty;
        var bits = 0;
        var value = 0;
        var chars = new char[(bytes.Length * 8 + 4) / 5];
        var i = 0;
        foreach (var b in bytes)
        {
            value = (value << 8) | b;
            bits += 8;
            while (bits >= 5)
            {
                bits -= 5;
                chars[i++] = Alphabet[(value >> bits) & 0x1F];
            }
        }
        if (bits > 0)
        {
            chars[i++] = Alphabet[(value << (5 - bits)) & 0x1F];
        }
        return new string(chars, 0, i);
    }

    /// <summary>
    /// Decode a Crockford base32 string back to bytes. Throws on invalid input.
    /// </summary>
    public static byte[] Decode(string input)
    {
        if (string.IsNullOrEmpty(input)) return Array.EmptyByte();
        // Normalize: uppercase, strip padding and dashes/spaces.
        var normalized = input.ToUpperInvariant().Replace("-", "").Replace(" ", "").TrimEnd('=');
        var output = new List<byte>(normalized.Length * 5 / 8 + 1);
        var bits = 0;
        var value = 0;
        foreach (var c in normalized)
        {
            var idx = Alphabet.IndexOf(c);
            if (idx < 0) throw new ArgumentException($"Invalid Crockford base32 character: {c}");
            value = (value << 5) | idx;
            bits += 5;
            if (bits >= 8)
            {
                bits -= 8;
                output.Add((byte)((value >> bits) & 0xFF));
            }
        }
        return output.ToArray();
    }
}

internal static class Array
{
    public static byte[] EmptyByte() => System.Array.Empty<byte>();
}
