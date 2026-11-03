using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using YfgDesk.Shared.Protocol;

namespace YfgDesk.Client.Services;

/// <summary>
/// <para>
/// REST client for <c>POST /api/turn-credentials</c>. Caches the returned
/// short-lived credentials until 5 minutes before expiry, then re-fetches
/// on the next call.
/// </para>
/// <para>
/// The server may return <c>{ turnAvailable: false, urls: [] }</c> if TURN
/// is not configured. In that case, <see cref="GetCredentialsAsync"/>
/// returns <c>null</c> — and the orchestrator must skip Tier 4.
/// </para>
/// </summary>
public sealed class TurnClient : ITurnClient
{
    private static readonly JsonSerializerOptions JsonOpts = JsonOptions.Default;
    private readonly string _serverBaseUrl;
    private readonly Func<string?> _bearerProvider;
    private readonly HttpClient _http;
    private readonly object _cacheLock = new();
    private TurnBundle? _cached;
    private DateTime _cachedRefreshAt;

    /// <summary>Refresh 5 minutes before the credential expiry.</summary>
    private static readonly TimeSpan RefreshMargin = TimeSpan.FromMinutes(5);

    public TurnClient(string serverBaseUrl, Func<string?> bearerProvider, HttpClient? http = null)
    {
        _serverBaseUrl = serverBaseUrl ?? throw new ArgumentNullException(nameof(serverBaseUrl));
        _bearerProvider = bearerProvider ?? throw new ArgumentNullException(nameof(bearerProvider));
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
    }

    public async Task<TurnBundle?> GetCredentialsAsync(CancellationToken ct = default)
    {
        lock (_cacheLock)
        {
            if (_cached is not null && DateTime.UtcNow < _cachedRefreshAt)
                return _cached;
        }

        using var req = new HttpRequestMessage(HttpMethod.Post, $"{_serverBaseUrl.TrimEnd('/')}/api/turn-credentials");
        var bearer = _bearerProvider();
        if (!string.IsNullOrEmpty(bearer))
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        req.Content = JsonContent.Create(new { }, options: JsonOpts);

        using var resp = await _http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode) return null;
        var body = await resp.Content.ReadFromJsonAsync<TurnCredentialsResponse>(JsonOpts, ct);
        if (body is null) return null;

        if (!body.TurnAvailable || body.Urls is null || body.Urls.Length == 0) return null;

        var bundle = new TurnBundle(
            body.Username ?? string.Empty,
            body.Credential ?? string.Empty,
            body.Urls,
            body.ExpiresAt);

        lock (_cacheLock)
        {
            _cached = bundle;
            _cachedRefreshAt = bundle.ExpiresAt.Subtract(RefreshMargin);
            if (_cachedRefreshAt < DateTime.UtcNow) _cachedRefreshAt = DateTime.UtcNow.AddSeconds(30);
        }
        return bundle;
    }

    /// <summary>Clear cached credentials (e.g. on session end).</summary>
    public void InvalidateCache()
    {
        lock (_cacheLock)
        {
            _cached = null;
            _cachedRefreshAt = DateTime.MinValue;
        }
    }

    private sealed class TurnCredentialsResponse
    {
        [JsonPropertyName("turnAvailable")] public bool TurnAvailable { get; set; }
        [JsonPropertyName("username")] public string? Username { get; set; }
        [JsonPropertyName("credential")] public string? Credential { get; set; }
        [JsonPropertyName("urls")] public string[]? Urls { get; set; }
        [JsonPropertyName("expiresAt")] public DateTime ExpiresAt { get; set; }
    }
}

/// <summary>
/// HTTP-backed implementation of <see cref="IServerLookupClient"/> that calls
/// <c>POST /api/lookup</c> on the central server. Bearer-auth via header.
/// </summary>
public sealed class ServerLookupClient : IServerLookupClient
{
    private static readonly JsonSerializerOptions JsonOpts = JsonOptions.Default;
    private readonly string _serverBaseUrl;
    private readonly Func<string?> _bearerProvider;
    private readonly HttpClient _http;

    public ServerLookupClient(string serverBaseUrl, Func<string?> bearerProvider, HttpClient? http = null)
    {
        _serverBaseUrl = serverBaseUrl ?? throw new ArgumentNullException(nameof(serverBaseUrl));
        _bearerProvider = bearerProvider ?? throw new ArgumentNullException(nameof(bearerProvider));
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
    }

    public async Task<LookupResponse?> LookupAsync(string targetId, CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{_serverBaseUrl.TrimEnd('/')}/api/lookup");
        var bearer = _bearerProvider();
        if (!string.IsNullOrEmpty(bearer))
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        req.Content = JsonContent.Create(new { targetId }, options: JsonOpts);

        using var resp = await _http.SendAsync(req, ct);
        if (resp.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        if (!resp.IsSuccessStatusCode) return null;
        var body = await resp.Content.ReadFromJsonAsync<LookupResponseBody>(JsonOpts, ct);
        if (body is null) return null;
        return new LookupResponse(
            body.Ip ?? string.Empty,
            body.Port,
            body.Online,
            body.LastSeen,
            body.PublicIps ?? Array.Empty<string>(),
            body.ListenPorts ?? Array.Empty<string>(),
            body.DeviceId ?? targetId,
            body.DisplayName ?? string.Empty);
    }

    private sealed class LookupResponseBody
    {
        [JsonPropertyName("ip")] public string? Ip { get; set; }
        [JsonPropertyName("port")] public int? Port { get; set; }
        [JsonPropertyName("online")] public bool Online { get; set; }
        [JsonPropertyName("lastSeen")] public DateTime LastSeen { get; set; }
        [JsonPropertyName("publicIps")] public string[]? PublicIps { get; set; }
        [JsonPropertyName("listenPorts")] public string[]? ListenPorts { get; set; }
        [JsonPropertyName("deviceId")] public string? DeviceId { get; set; }
        [JsonPropertyName("displayName")] public string? DisplayName { get; set; }
    }
}
