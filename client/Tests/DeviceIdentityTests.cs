using System.Security.Cryptography;
using System.Text.Json;
using YfgDesk.Client.Services;
using YfgDesk.Shared.Crypto;

namespace YfgDesk.Client.Tests;

public class DeviceIdentityTests
{
    [Fact]
    public async Task GeneratedKeypair_CanSignAndVerify()
    {
        // Round-trip: generate → sign → verify with the SPKI public key.
        var (key, spkiB64, pkcs8B64) = DeviceIdentity.GenerateKeyPair();

        // The SPKI must be a base64 string starting with the P-256 SPKI prefix.
        Assert.True(spkiB64.Length > 60);

        // Verify the signature independently using ECDsa verifier.
        using var verifier = ECDsa.Create();
        verifier.ImportSubjectPublicKeyInfo(Convert.FromBase64String(spkiB64), out _);

        var nonce = "deadbeef";
        var msgBytes = System.Text.Encoding.UTF8.GetBytes(nonce);
        var sig = key.SignData(msgBytes, HashAlgorithmName.SHA256);
        Assert.True(verifier.VerifyData(msgBytes, sig, HashAlgorithmName.SHA256));

        key.Dispose();
    }

    [Fact]
    public async Task Persist_Roundtrips_Through_SecureStorage()
    {
        var storage = new InMemorySecureStorage();
        var tmpPath = Path.Combine(Path.GetTempPath(), $"yfg-test-{Guid.NewGuid():N}.bin");
        try
        {
            var (key, spkiB64, pkcs8B64) = DeviceIdentity.GenerateKeyPair();
            var stored = new DeviceIdentity.StoredIdentity
            {
                DeviceId = "123456789",
                BearerToken = "yfg.testtoken",
                BearerExpiresAtEpochMs = DateTimeOffset.UtcNow.AddHours(2).ToUnixTimeMilliseconds(),
                PrivateKeyPkcs8B64 = pkcs8B64,
                PublicKeySpkiB64 = spkiB64,
                DisplayName = "test-pc",
                CreatedAtEpochMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            };
            var identity = new DeviceIdentity(stored, key, storage, tmpPath);
            await identity.PersistAsync();

            // Re-load from disk.
            var loaded = await DeviceIdentity.LoadAsync(storage, tmpPath);
            Assert.NotNull(loaded);
            Assert.Equal("123456789", loaded!.DeviceId);
            Assert.Equal("yfg.testtoken", loaded.BearerToken);
            Assert.Equal("test-pc", loaded.DisplayName);
            Assert.Equal(spkiB64, loaded.PublicKeySpkiB64);

            // Sign with the loaded key and verify with the original SPKI.
            using var v = ECDsa.Create();
            v.ImportSubjectPublicKeyInfo(Convert.FromBase64String(spkiB64), out _);
            var sig = loaded.Sign("hello");
            Assert.True(v.VerifyData(System.Text.Encoding.UTF8.GetBytes("hello"),
                Convert.FromBase64String(sig), HashAlgorithmName.SHA256));

            loaded.Dispose();
        }
        finally
        {
            try { File.Delete(tmpPath); } catch { /* best-effort */ }
        }
    }

    [Fact]
    public async Task Wipe_Clears_Identity()
    {
        var storage = new InMemorySecureStorage();
        var tmpPath = Path.Combine(Path.GetTempPath(), $"yfg-test-{Guid.NewGuid():N}.bin");

        var (key, spkiB64, pkcs8B64) = DeviceIdentity.GenerateKeyPair();
        var stored = new DeviceIdentity.StoredIdentity
        {
            DeviceId = "987654321",
            BearerToken = "yfg.tok",
            BearerExpiresAtEpochMs = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeMilliseconds(),
            PrivateKeyPkcs8B64 = pkcs8B64,
            PublicKeySpkiB64 = spkiB64,
            DisplayName = "wipe-pc",
            CreatedAtEpochMs = 0,
        };
        var identity = new DeviceIdentity(stored, key, storage, tmpPath);
        await identity.PersistAsync();

        await identity.WipeAsync();
        Assert.Equal(string.Empty, identity.DeviceId);
        Assert.Equal(string.Empty, identity.BearerToken);

        var loaded = await DeviceIdentity.LoadAsync(storage, tmpPath);
        Assert.Null(loaded);
        identity.Dispose();
    }

    [Fact]
    public void DefaultIdentityFilePath_Produces_PlatformSpecific_Path()
    {
        var path = DeviceIdentity.DefaultIdentityFilePath();
        Assert.False(string.IsNullOrEmpty(path));
        if (OperatingSystem.IsWindows())
            Assert.Contains("YFGDESK", path);
        else
            Assert.Contains(".config/yfgdesk", path.Replace('\\', '/'));
    }

    [Fact]
    public async Task LoadAsync_ReturnsNull_For_MissingFile()
    {
        var storage = new InMemorySecureStorage();
        var path = "/nonexistent/identity-" + Guid.NewGuid().ToString("N") + ".bin";
        var loaded = await DeviceIdentity.LoadAsync(storage, path);
        Assert.Null(loaded);
    }
}
