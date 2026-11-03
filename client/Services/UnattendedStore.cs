using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace YfgDesk.Client.Services;

/// <summary>
/// Off-by-default unattended-access store. Holds an Argon2id verifier (RFC 9106)
/// for the host's unattended-access password.
///
/// <para>
/// <b>The verifier NEVER leaves this machine.</b> It is stored locally via
/// <see cref="ISecureStorage"/> (DPAPI on Windows). The server never sees
/// the password, the verifier, or any related hash.
/// </para>
///
/// <para>
/// Lockout: after <c>MaxFailures</c> (default 5) failed attempts, the store
/// refuses to verify for <c>LockoutDuration</c> (default 15 min). The lockout
/// state is in-memory only — restarting the client resets it. (This is
/// intentional: a long-lived lockout would create a DoS that punishes the
/// legitimate owner.)
/// </para>
///
/// <para>
/// <b>Linux stub:</b> on the headless <c>net10.0</c> target, every method
/// throws <see cref="PlatformNotSupportedException"/> — the Argon2 native
/// library and DPAPI are unavailable. The class still compiles so the rest of
/// the client (and the unit tests) can run on Linux.
/// </para>
/// </summary>
public sealed class UnattendedStore
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    private readonly ISecureStorage _storage;
    private readonly string _filePath;
    private readonly Func<long> _clock;

    private readonly object _lock = new();
    private int _failures;
    private long _lockoutUntilMs;

    public int MaxFailures { get; set; } = 5;
    public TimeSpan LockoutDuration { get; set; } = TimeSpan.FromMinutes(15);

    public UnattendedStore(ISecureStorage storage, string? filePath = null, Func<long>? clock = null)
    {
        _storage = storage ?? throw new ArgumentNullException(nameof(storage));
        _filePath = filePath ?? DefaultStorePath();
        _clock = clock ?? (() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
    }

    public static string DefaultStorePath()
    {
        string dir;
        if (OperatingSystem.IsWindows())
            dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "YFGDESK");
        else
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            dir = Path.Combine(home, ".config", "yfgdesk");
        }
        return Path.Combine(dir, "unattended.bin");
    }

    /// <summary>True if the verifier file is present on disk.</summary>
    public async Task<bool> IsEnabledAsync(CancellationToken ct = default)
    {
        var bytes = await _storage.ReadAsync(_filePath, ct);
        return bytes is not null && bytes.Length > 0;
    }

    /// <summary>
    /// Enable unattended access by deriving an Argon2id verifier from
    /// <paramref name="password"/> and persisting it locally.
    /// </summary>
    public Task EnableAsync(string password, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(password)) throw new ArgumentException("password required", nameof(password));
        if (password.Length < 8) throw new ArgumentException("password must be at least 8 characters", nameof(password));
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Argon2 verifier requires Windows (native lib + DPAPI).");

        return EnableCoreAsync(password, ct);
    }

    /// <summary>
    /// Disable unattended access by removing the verifier file.
    /// </summary>
    public async Task DisableAsync(CancellationToken ct = default)
    {
        await _storage.WriteAsync(_filePath, Array.Empty<byte>(), ct);
        _storage.Delete(_filePath);
        lock (_lock)
        {
            _failures = 0;
            _lockoutUntilMs = 0;
        }
    }

    /// <summary>
    /// Verify a password against the stored verifier. Returns true on match.
    /// Increments the failure counter on mismatch and applies lockout when
    /// <see cref="MaxFailures"/> is reached.
    /// </summary>
    public Task<bool> VerifyAsync(string password, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(password)) return Task.FromResult(false);
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Argon2 verifier requires Windows (native lib + DPAPI).");
        return VerifyCoreAsync(password, ct);
    }

    /// <summary>
    /// Clear the lockout counter (e.g. after a successful verify).
    /// </summary>
    public void ClearLockout()
    {
        lock (_lock) { _failures = 0; _lockoutUntilMs = 0; }
    }

    /// <summary>
    /// Returns the number of consecutive failures since the last success.
    /// </summary>
    public int FailureCount { get { lock (_lock) return _failures; } }

    /// <summary>
    /// Returns the epoch-ms timestamp when the lockout expires, or 0 if not locked.
    /// </summary>
    public long LockoutUntilMs { get { lock (_lock) return _lockoutUntilMs; } }

    // ---- on-disk format ----

    private sealed class StoredVerifier
    {
        [JsonPropertyName("algo")] public string Algo { get; set; } = "argon2id";
        [JsonPropertyName("version")] public int Version { get; set; } = 1;
        [JsonPropertyName("salt")] public string SaltB64 { get; set; } = "";
        [JsonPropertyName("hash")] public string HashB64 { get; set; } = "";
        [JsonPropertyName("iterations")] public int Iterations { get; set; }
        [JsonPropertyName("memoryKiB")] public int MemoryKiB { get; set; }
        [JsonPropertyName("parallelism")] public int Parallelism { get; set; }
        [JsonPropertyName("outputLengthBytes")] public int OutputLengthBytes { get; set; }
        [JsonPropertyName("createdAtEpochMs")] public long CreatedAtEpochMs { get; set; }
    }

    // ---- Windows-only Argon2 implementation ----
#if NET10_0_WINDOWS
    private async Task EnableCoreAsync(string password, CancellationToken ct)
    {
        // 16-byte random salt, RFC 9106 Argon2id parameters tuned for ~250ms on a modern CPU.
        Span<byte> salt = stackalloc byte[16];
        RandomNumberGenerator.Fill(salt);

        const int iterations = 3;
        const int memoryKiB = 64 * 1024;   // 64 MiB
        const int parallelism = 2;
        const int outLen = 32;

        var hash = Argon2Hash(password, salt, iterations, memoryKiB, parallelism, outLen);

        var stored = new StoredVerifier
        {
            SaltB64 = Convert.ToBase64String(salt.ToArray()),
            HashB64 = Convert.ToBase64String(hash),
            Iterations = iterations,
            MemoryKiB = memoryKiB,
            Parallelism = parallelism,
            OutputLengthBytes = outLen,
            CreatedAtEpochMs = _clock(),
        };
        var json = JsonSerializer.Serialize(stored, JsonOpts);
        await _storage.WriteAsync(_filePath, Encoding.UTF8.GetBytes(json), ct);
        lock (_lock) { _failures = 0; _lockoutUntilMs = 0; }
    }

    private async Task<bool> VerifyCoreAsync(string password, CancellationToken ct)
    {
        // Lockout check.
        lock (_lock)
        {
            if (_lockoutUntilMs != 0 && _clock() < _lockoutUntilMs)
                return false; // still locked
        }

        var bytes = await _storage.ReadAsync(_filePath, ct);
        if (bytes is null || bytes.Length == 0) return false;
        StoredVerifier? stored;
        try
        {
            stored = JsonSerializer.Deserialize<StoredVerifier>(Encoding.UTF8.GetString(bytes), JsonOpts);
        }
        catch { return false; }
        if (stored is null || stored.Algo != "argon2id") return false;

        byte[] salt;
        byte[] expectedHash;
        try
        {
            salt = Convert.FromBase64String(stored.SaltB64);
            expectedHash = Convert.FromBase64String(stored.HashB64);
        }
        catch { return false; }
        if (expectedHash.Length != stored.OutputLengthBytes) return false;

        var actual = Argon2Hash(password, salt, stored.Iterations, stored.MemoryKiB, stored.Parallelism, stored.OutputLengthBytes);

        // Constant-time compare.
        bool ok = ConstantTimeEquals(actual, expectedHash);

        lock (_lock)
        {
            if (ok) { _failures = 0; _lockoutUntilMs = 0; }
            else
            {
                _failures++;
                if (_failures >= MaxFailures)
                {
                    _lockoutUntilMs = _clock() + (long)LockoutDuration.TotalMilliseconds;
                    _failures = 0; // reset; next window starts after lockout
                }
            }
        }
        return ok;
    }

    private static byte[] Argon2Hash(string password, byte[] salt, int iterations, int memoryKiB, int parallelism, int outLen)
    {
        var pwdBytes = Encoding.UTF8.GetBytes(password);
        var argon = new Konscious.Security.Cryptography.Argon2id(pwdBytes)
        {
            Salt = salt,
            DegreeOfParallelism = parallelism,
            MemorySize = memoryKiB,
            Iterations = iterations,
        };
        return argon.GetBytes(outLen);
    }

    private static bool ConstantTimeEquals(byte[] a, byte[] b)
    {
        if (a.Length != b.Length) return false;
        int diff = 0;
        for (int i = 0; i < a.Length; i++) diff |= a[i] ^ b[i];
        return diff == 0;
    }
#else
    // Linux / headless: no Argon2 native lib. These implementations are never
    // reached at runtime because the public EnableAsync/VerifyAsync check
    // OperatingSystem.IsWindows() and throw PNSE.
    private Task EnableCoreAsync(string password, CancellationToken ct)
        => throw new PlatformNotSupportedException("Argon2 verifier requires Windows.");
    private Task<bool> VerifyCoreAsync(string password, CancellationToken ct)
        => throw new PlatformNotSupportedException("Argon2 verifier requires Windows.");
#endif
}
