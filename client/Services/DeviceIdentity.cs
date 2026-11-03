using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using YfgDesk.Shared.Crypto;
using YfgDesk.Shared.Protocol;

namespace YfgDesk.Client.Services;

/// <summary>
/// P-256 ECDSA device identity. Stored as PKCS8 private + SPKI public,
/// DPAPI-protected on Windows, plaintext-0600 on Linux.
///
/// The enrollment flow is:
/// <list type="number">
///   <item>Generate P-256 keypair (on first run).</item>
///   <item><c>POST /api/enroll/challenge</c> with <c>{ publicKey }</c> → server returns <c>{ challengeId, nonce, expiresAt }</c>.</item>
///   <item>Sign the nonce (UTF-8 bytes) with ECDSA P-256 SHA-256, DER-encoded, base64.</item>
///   <item><c>POST /api/enroll/register</c> with <c>{ challengeId, signature }</c> → server returns <c>{ deviceId, bearerToken, expiresAt }</c>.</item>
///   <item>Persist deviceId + bearerToken + keypair to <see cref="SecureStorage"/>.</item>
/// </list>
///
/// Bearer is NEVER sent in query strings — always <c>Authorization: Bearer ...</c>.
/// </summary>
public sealed class DeviceIdentity : IDisposable
{
    private static readonly JsonSerializerOptions JsonOpts = JsonOptions.Default;

    /// <summary>
    /// On-disk identity file format. Persisted via <see cref="ISecureStorage"/>.
    /// </summary>
    public sealed class StoredIdentity
    {
        [JsonPropertyName("deviceId")] public string DeviceId { get; set; } = string.Empty;
        [JsonPropertyName("bearerToken")] public string BearerToken { get; set; } = string.Empty;
        [JsonPropertyName("bearerExpiresAtEpochMs")] public long BearerExpiresAtEpochMs { get; set; }
        [JsonPropertyName("privateKeyPkcs8B64")] public string PrivateKeyPkcs8B64 { get; set; } = string.Empty;
        [JsonPropertyName("publicKeySpkiB64")] public string PublicKeySpkiB64 { get; set; } = string.Empty;
        [JsonPropertyName("displayName")] public string DisplayName { get; set; } = string.Empty;
        [JsonPropertyName("createdAtEpochMs")] public long CreatedAtEpochMs { get; set; }
    }

    private readonly ISecureStorage _storage;
    private readonly string _identityFilePath;
    private readonly Func<long> _clock;

    private ECDsa _key;
    private StoredIdentity _stored;

    /// <summary>
    /// Construct from an already-loaded key + stored identity. Used by tests
    /// and by <see cref="LoadAsync"/>.
    /// </summary>
    public DeviceIdentity(StoredIdentity stored, ECDsa key, ISecureStorage storage, string filePath, Func<long>? clock = null)
    {
        _stored = stored ?? throw new ArgumentNullException(nameof(stored));
        _key = key ?? throw new ArgumentNullException(nameof(key));
        _storage = storage ?? throw new ArgumentNullException(nameof(storage));
        _identityFilePath = filePath ?? throw new ArgumentNullException(nameof(filePath));
        _clock = clock ?? (() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
    }

    public string DeviceId => _stored.DeviceId;
    public string BearerToken => _stored.BearerToken;
    public string PublicKeySpkiB64 => _stored.PublicKeySpkiB64;
    public string DisplayName => _stored.DisplayName;
    public DateTimeOffset BearerExpiresAt => DateTimeOffset.FromUnixTimeMilliseconds(_stored.BearerExpiresAtEpochMs);

    public bool IsBearerExpired => _clock() >= _stored.BearerExpiresAtEpochMs;

    /// <summary>
    /// Default identity file path for the current OS.
    /// </summary>
    public static string DefaultIdentityFilePath()
    {
        string dir;
        if (OperatingSystem.IsWindows())
        {
            dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "YFGDESK");
        }
        else
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            dir = Path.Combine(home, ".config", "yfgdesk");
        }
        return Path.Combine(dir, "identity.bin");
    }

    /// <summary>
    /// Load an existing identity from <paramref name="filePath"/> (defaults to
    /// <see cref="DefaultIdentityFilePath"/>). Returns null if no identity exists.
    /// </summary>
    public static async Task<DeviceIdentity?> LoadAsync(
        ISecureStorage storage,
        string? filePath = null,
        Func<long>? clock = null,
        CancellationToken ct = default)
    {
        var path = filePath ?? DefaultIdentityFilePath();
        var cipher = await storage.ReadAsync(path, ct);
        if (cipher is null || cipher.Length == 0) return null;

        StoredIdentity? stored;
        try
        {
            var json = Encoding.UTF8.GetString(cipher);
            stored = JsonSerializer.Deserialize<StoredIdentity>(json, JsonOpts);
        }
        catch
        {
            return null;
        }
        if (stored is null || string.IsNullOrEmpty(stored.PrivateKeyPkcs8B64)) return null;

        var key = ECDsa.Create();
        try
        {
            var pkcs8 = Convert.FromBase64String(stored.PrivateKeyPkcs8B64);
            key.ImportPkcs8PrivateKey(pkcs8, out _);
            // Verify curve is P-256.
            var p = key.ExportParameters(false);
            if (p.Curve.Oid?.Value != "1.2.840.10045.3.1.7")
            {
                key.Dispose();
                return null;
            }
        }
        catch
        {
            key.Dispose();
            return null;
        }

        return new DeviceIdentity(stored, key, storage, path, clock);
    }

    /// <summary>
    /// Generate a fresh P-256 keypair WITHOUT persisting. Used by tests and
    /// by <see cref="EnrollAsync"/> on first run.
    /// </summary>
    public static (ECDsa key, string spkiB64, string pkcs8B64) GenerateKeyPair()
    {
        var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var spki = key.ExportSubjectPublicKeyInfo();
        var pkcs8 = key.ExportPkcs8PrivateKey();
        return (key, Convert.ToBase64String(spki), Convert.ToBase64String(pkcs8));
    }

    /// <summary>
    /// Sign a UTF-8 message with the private key (ECDSA P-256 SHA-256, DER).
    /// Returns base64-encoded DER signature.
    /// </summary>
    public string Sign(string message)
    {
        var msgBytes = Encoding.UTF8.GetBytes(message);
        var sig = _key.SignData(msgBytes, HashAlgorithmName.SHA256);
        return Convert.ToBase64String(sig);
    }

    /// <summary>
    /// Enroll with the server. Generates a fresh keypair if none exists,
    /// calls <c>POST /api/enroll/challenge</c> then <c>POST /api/enroll/register</c>,
    /// persists the identity. Returns the new <see cref="DeviceIdentity"/>.
    /// </summary>
    public static async Task<DeviceIdentity> EnrollAsync(
        string serverBaseUrl,
        ISecureStorage storage,
        HttpClient http,
        string displayName,
        string? filePath = null,
        Func<long>? clock = null,
        CancellationToken ct = default)
    {
        var path = filePath ?? DefaultIdentityFilePath();
        var existing = await LoadAsync(storage, path, clock, ct);
        var key = existing?._key ?? GenerateKeyPair().key;
        var spkiB64 = existing?.PublicKeySpkiB64
            ?? Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());
        var pkcs8B64 = existing?.PublicKeySpkiB64 is null
            ? Convert.ToBase64String(key.ExportPkcs8PrivateKey())
            : Convert.ToBase64String(key.ExportPkcs8PrivateKey());

        var c = clock ?? (() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

        // 1) challenge
        var challengeReq = new { publicKey = spkiB64 };
        using var challengeResp = await http.PostAsJsonAsync(
            $"{serverBaseUrl.TrimEnd('/')}/api/enroll/challenge", challengeReq, JsonOpts, ct);
        challengeResp.EnsureSuccessStatusCode();
        var challenge = await challengeResp.Content.ReadFromJsonAsync<ChallengeResponse>(JsonOpts, ct)
            ?? throw new InvalidOperationException("server returned empty challenge response");

        // 2) sign nonce
        var identity = existing ?? new DeviceIdentity(
            new StoredIdentity
            {
                DeviceId = "",
                BearerToken = "",
                BearerExpiresAtEpochMs = 0,
                PrivateKeyPkcs8B64 = pkcs8B64,
                PublicKeySpkiB64 = spkiB64,
                DisplayName = displayName,
                CreatedAtEpochMs = c(),
            },
            key, storage, path, c);
        var signature = identity.Sign(challenge.Nonce);

        // 3) register
        var registerReq = new { challengeId = challenge.ChallengeId, signature };
        using var registerResp = await http.PostAsJsonAsync(
            $"{serverBaseUrl.TrimEnd('/')}/api/enroll/register", registerReq, JsonOpts, ct);
        registerResp.EnsureSuccessStatusCode();
        var registered = await registerResp.Content.ReadFromJsonAsync<RegisterResponse>(JsonOpts, ct)
            ?? throw new InvalidOperationException("server returned empty register response");

        identity._stored.DeviceId = registered.DeviceId;
        identity._stored.BearerToken = registered.BearerToken;
        identity._stored.BearerExpiresAtEpochMs = ((DateTimeOffset)registered.ExpiresAt).ToUnixTimeMilliseconds();

        await identity.PersistAsync(ct);
        return identity;
    }

    /// <summary>
    /// Renew the bearer token. This is functionally the same as re-running
    /// enroll with the same keypair — the server returns a fresh bearer for
    /// the existing deviceId.
    /// </summary>
    public async Task RenewBearerAsync(string serverBaseUrl, HttpClient http, CancellationToken ct = default)
    {
        var challengeReq = new { publicKey = _stored.PublicKeySpkiB64 };
        using var challengeResp = await http.PostAsJsonAsync(
            $"{serverBaseUrl.TrimEnd('/')}/api/enroll/challenge", challengeReq, JsonOpts, ct);
        challengeResp.EnsureSuccessStatusCode();
        var challenge = await challengeResp.Content.ReadFromJsonAsync<ChallengeResponse>(JsonOpts, ct)
            ?? throw new InvalidOperationException("server returned empty challenge response");

        var signature = Sign(challenge.Nonce);
        var registerReq = new { challengeId = challenge.ChallengeId, signature };
        using var registerResp = await http.PostAsJsonAsync(
            $"{serverBaseUrl.TrimEnd('/')}/api/enroll/register", registerReq, JsonOpts, ct);
        registerResp.EnsureSuccessStatusCode();
        var registered = await registerResp.Content.ReadFromJsonAsync<RegisterResponse>(JsonOpts, ct)
            ?? throw new InvalidOperationException("server returned empty register response");

        if (!string.IsNullOrEmpty(registered.DeviceId))
            _stored.DeviceId = registered.DeviceId;
        _stored.BearerToken = registered.BearerToken;
        _stored.BearerExpiresAtEpochMs = ((DateTimeOffset)registered.ExpiresAt).ToUnixTimeMilliseconds();

        await PersistAsync(ct);
    }

    /// <summary>
    /// Persist the current identity back to disk.
    /// </summary>
    public async Task PersistAsync(CancellationToken ct = default)
    {
        var json = JsonSerializer.Serialize(_stored, JsonOpts);
        var bytes = Encoding.UTF8.GetBytes(json);
        await _storage.WriteAsync(_identityFilePath, bytes, ct);
    }

    /// <summary>
    /// Wipe the local identity (irreversible). The server-side deviceId stays
    /// — re-enrollment with a fresh keypair will allocate a new deviceId.
    /// </summary>
    public async Task WipeAsync(CancellationToken ct = default)
    {
        _storage.Delete(_identityFilePath);
        _stored.DeviceId = string.Empty;
        _stored.BearerToken = string.Empty;
        _stored.BearerExpiresAtEpochMs = 0;
        await Task.CompletedTask;
    }

    public void Dispose() => _key.Dispose();

    // ---- server response DTOs (camelCase via JsonOptions.Web) ----

    public sealed class ChallengeResponse
    {
        [JsonPropertyName("challengeId")] public string ChallengeId { get; set; } = "";
        [JsonPropertyName("nonce")] public string Nonce { get; set; } = "";
        [JsonPropertyName("expiresAt")] public DateTime ExpiresAt { get; set; }
    }

    public sealed class RegisterResponse
    {
        [JsonPropertyName("deviceId")] public string DeviceId { get; set; } = "";
        [JsonPropertyName("bearerToken")] public string BearerToken { get; set; } = "";
        [JsonPropertyName("expiresAt")] public DateTime ExpiresAt { get; set; }
    }

    /// <summary>
    /// Build an <see cref="HttpClient"/> with the bearer token pre-attached.
    /// The header is added lazily by <see cref="BearerMessageHandler"/>.
    /// </summary>
    public static HttpClient CreateHttpClient(Func<string?> bearerProvider)
    {
        var handler = new BearerMessageHandler(bearerProvider)
        {
            InnerHandler = new HttpClientHandler(),
        };
        return new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
    }

    /// <summary>
    /// Delegating handler that injects the bearer into the Authorization header.
    /// The bearer is NEVER in the query string.
    /// </summary>
    public sealed class BearerMessageHandler : DelegatingHandler
    {
        private readonly Func<string?> _bearerProvider;
        public BearerMessageHandler(Func<string?> bearerProvider) => _bearerProvider = bearerProvider;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var bearer = _bearerProvider();
            if (!string.IsNullOrEmpty(bearer))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
            }
            return base.SendAsync(request, ct);
        }
    }
}
