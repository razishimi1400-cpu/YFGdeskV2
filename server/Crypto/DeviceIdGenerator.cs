using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using YfgDesk.Server.Data;

namespace YfgDesk.Server.Crypto;

/// <summary>
/// Generates a 9-digit random device identifier and retries on DB collision.
/// Format: 9 decimal digits, zero-padded ("000000000".."999999999").
/// </summary>
public sealed class DeviceIdGenerator
{
    /// <summary>
    /// Maximum attempts to find a unique ID before giving up.
    /// </summary>
    private const int MaxAttempts = 32;

    /// <summary>
    /// Generate a unique 9-digit DeviceId not already present in <paramref name="db"/>.
    /// Throws <see cref="InvalidOperationException"/> after <see cref="MaxAttempts"/> collisions.
    /// </summary>
    public async Task<string> GenerateUniqueAsync(YfgDeskDbContext db, CancellationToken ct = default)
    {
        for (var i = 0; i < MaxAttempts; i++)
        {
            var n = RandomNumberGenerator.GetInt32(0, 1_000_000_000);
            var id = n.ToString("D9");
            // Try-catch on INSERT happens upstream; here we pre-check existence.
            var exists = await db.Devices.AsNoTracking().AnyAsync(d => d.DeviceId == id, ct);
            if (!exists) return id;
        }
        throw new InvalidOperationException("Could not allocate a unique device id after 32 attempts");
    }

    /// <summary>
    /// Format a numeric ID into 9-digit zero-padded form.
    /// </summary>
    public static string Format(int n)
    {
        if (n < 0 || n >= 1_000_000_000) throw new ArgumentOutOfRangeException(nameof(n));
        return n.ToString("D9");
    }
}
