using System.IO;
using System.Security.Cryptography;

namespace YfgDesk.Client.Services;

/// <summary>
/// DPAPI-protected (Windows) or plaintext-0600 (Linux) wrapper around
/// sensitive file storage. Used by <see cref="DeviceIdentity"/> and
/// <see cref="UnattendedStore"/> to persist private keys and verifiers.
///
/// The Windows path uses <c>System.Security.Cryptography.ProtectedData</c>
/// (DPAPI <c>CurrentUser</c> scope). The Linux path is plaintext with
/// 0600 file mode — appropriate for local-only secrets under the user's
/// home directory. Neither path ever sends secrets to the server.
/// </summary>
public interface ISecureStorage
{
    Task<byte[]?> ReadAsync(string path, CancellationToken ct = default);
    Task WriteAsync(string path, byte[] plaintext, CancellationToken ct = default);
    void Delete(string path);
}

/// <summary>
/// Default implementation. Switches between DPAPI and plaintext at runtime
/// (via <see cref="OperatingSystem.IsWindows"/>) so the same code compiles
/// on both the <c>net10.0-windows</c> and headless <c>net10.0</c> targets.
/// </summary>
public sealed class SecureStorage : ISecureStorage
{
    public static ISecureStorage Default { get; } = new SecureStorage();

    public async Task<byte[]?> ReadAsync(string path, CancellationToken ct = default)
    {
        if (!File.Exists(path)) return null;
        var cipher = await File.ReadAllBytesAsync(path, ct);
        if (cipher.Length == 0) return null;
        return Unprotect(cipher);
    }

    public async Task WriteAsync(string path, byte[] plaintext, CancellationToken ct = default)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        var cipher = Protect(plaintext);
        var tmp = path + ".tmp";
        await File.WriteAllBytesAsync(tmp, cipher, ct);

        if (File.Exists(path))
            File.Replace(tmp, path, destinationBackupFileName: null);
        else
            File.Move(tmp, path);

        // Best-effort 0600 on Unix for the plaintext branch.
        if (!OperatingSystem.IsWindows())
        {
            try { File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite); }
            catch { /* not all filesystems support this */ }
        }
    }

    public void Delete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { /* ignore — best-effort */ }
    }

    // ---- platform branch ----

    private static byte[] Protect(byte[] plaintext)
    {
        if (!OperatingSystem.IsWindows()) return plaintext;
        return DpapiImpl.Protect(plaintext);
    }

    private static byte[]? Unprotect(byte[] cipher)
    {
        if (!OperatingSystem.IsWindows()) return cipher;
        return DpapiImpl.Unprotect(cipher);
    }

#if NET10_0_WINDOWS
    private static class DpapiImpl
    {
        public static byte[] Protect(byte[] plaintext)
        {
            try
            {
                return System.Security.Cryptography.ProtectedData.Protect(
                    plaintext, optionalEntropy: null, DataProtectionScope.CurrentUser);
            }
            catch (CryptographicException)
            {
                // DPAPI sometimes fails under service accounts; fall back to plaintext.
                return plaintext;
            }
        }

        public static byte[]? Unprotect(byte[] cipher)
        {
            try
            {
                return System.Security.Cryptography.ProtectedData.Unprotect(
                    cipher, optionalEntropy: null, DataProtectionScope.CurrentUser);
            }
            catch (CryptographicException)
            {
                // Either wasn't DPAPI-protected, or it's from a different user.
                return null;
            }
        }
    }
#else
    private static class DpapiImpl
    {
        // Not referenced on the headless target (operating-system guard above
        // short-circuits), but kept here so the file body compiles on both.
        public static byte[] Protect(byte[] plaintext) => plaintext;
        public static byte[]? Unprotect(byte[] cipher) => cipher;
    }
#endif
}

/// <summary>
/// In-memory <see cref="ISecureStorage"/> for unit tests. Never touches disk.
/// </summary>
public sealed class InMemorySecureStorage : ISecureStorage
{
    private readonly Dictionary<string, byte[]> _store = new();

    public Task<byte[]?> ReadAsync(string path, CancellationToken ct = default)
    {
        lock (_store)
        {
            return Task.FromResult(_store.TryGetValue(path, out var bytes) ? bytes : null);
        }
    }

    public Task WriteAsync(string path, byte[] plaintext, CancellationToken ct = default)
    {
        lock (_store) _store[path] = plaintext;
        return Task.CompletedTask;
    }

    public void Delete(string path)
    {
        lock (_store) _store.Remove(path);
    }
}
