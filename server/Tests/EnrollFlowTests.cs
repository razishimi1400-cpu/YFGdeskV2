using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using YfgDesk.Server.Data;
using YfgDesk.Shared.Protocol;

namespace YfgDesk.Server.Tests;

public sealed class EnrollFlowTests
{
    private static readonly JsonSerializerOptions Json = JsonOptions.Default;

    [Fact]
    public async Task Challenge_ReturnsChallengeIdAndNonce()
    {
        using var factory = new TestServerFactory();
        var client = factory.CreateClient();
        var (spki, _) = CryptoTestUtils.GenerateP256KeyPair();
        var body = JsonSerializer.Serialize(new { publicKey = spki }, Json);
        var resp = await client.PostAsync("/api/enroll/challenge",
            new StringContent(body, Encoding.UTF8, "application/json"));
        Assert.Equal(System.Net.HttpStatusCode.OK, resp.StatusCode);
        var json = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(json.TryGetProperty("challengeId", out var cid) && cid.GetString()!.StartsWith("c_"));
        Assert.True(json.TryGetProperty("nonce", out var nonce) && nonce.GetString()!.Length > 0);
        Assert.True(json.TryGetProperty("expiresAt", out _));
    }

    [Fact]
    public async Task Challenge_RejectsInvalidSpki()
    {
        using var factory = new TestServerFactory();
        var client = factory.CreateClient();
        var body = JsonSerializer.Serialize(new { publicKey = "not-base64!!" }, Json);
        var resp = await client.PostAsync("/api/enroll/challenge",
            new StringContent(body, Encoding.UTF8, "application/json"));
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Register_WithValidSignature_ReturnsDeviceIdAndBearer()
    {
        using var factory = new TestServerFactory();
        var client = factory.CreateClient();
        var (spki, key) = CryptoTestUtils.GenerateP256KeyPair();

        // 1. Challenge.
        var cbody = JsonSerializer.Serialize(new { publicKey = spki }, Json);
        var cresp = await client.PostAsync("/api/enroll/challenge",
            new StringContent(cbody, Encoding.UTF8, "application/json"));
        var cjson = await cresp.Content.ReadFromJsonAsync<JsonElement>();
        var challengeId = cjson.GetProperty("challengeId").GetString()!;
        var nonce = cjson.GetProperty("nonce").GetString()!;

        // 2. Sign and register.
        var sig = CryptoTestUtils.SignNonce(key, nonce);
        var rbody = JsonSerializer.Serialize(new { challengeId, signature = sig }, Json);
        var rresp = await client.PostAsync("/api/enroll/register",
            new StringContent(rbody, Encoding.UTF8, "application/json"));
        Assert.Equal(System.Net.HttpStatusCode.OK, rresp.StatusCode);
        var rjson = await rresp.Content.ReadFromJsonAsync<JsonElement>();
        var deviceId = rjson.GetProperty("deviceId").GetString()!;
        var bearer = rjson.GetProperty("bearerToken").GetString()!;
        Assert.Equal(9, deviceId.Length);
        Assert.StartsWith("yfg.", bearer);
        Assert.Equal(64 + 4, bearer.Length);

        // 3. Bearer is persisted as SHA-256 digest only — raw token never appears.
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<YfgDeskDbContext>();
        var dev = await db.Devices.FirstAsync(d => d.DeviceId == deviceId);
        Assert.Equal(64, dev.BearerDigest!.Length);
        Assert.DoesNotContain(bearer, dev.BearerDigest);
        // Ensure the raw token itself is not stored in any text column.
        Assert.DoesNotContain(bearer, dev.PublicKeySpki);
    }

    [Fact]
    public async Task Register_RejectsInvalidSignature()
    {
        using var factory = new TestServerFactory();
        var client = factory.CreateClient();
        var (spki, _) = CryptoTestUtils.GenerateP256KeyPair();

        var cbody = JsonSerializer.Serialize(new { publicKey = spki }, Json);
        var cresp = await client.PostAsync("/api/enroll/challenge",
            new StringContent(cbody, Encoding.UTF8, "application/json"));
        var cjson = await cresp.Content.ReadFromJsonAsync<JsonElement>();
        var challengeId = cjson.GetProperty("challengeId").GetString()!;

        var rbody = JsonSerializer.Serialize(new { challengeId, signature = "AAAAinvalidSig==" }, Json);
        var rresp = await client.PostAsync("/api/enroll/register",
            new StringContent(rbody, Encoding.UTF8, "application/json"));
        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, rresp.StatusCode);
    }

    [Fact]
    public async Task Lookup_RequiresBearer()
    {
        using var factory = new TestServerFactory();
        var client = factory.CreateClient();
        var body = JsonSerializer.Serialize(new { targetId = "123456789" }, Json);
        var resp = await client.PostAsync("/api/lookup",
            new StringContent(body, Encoding.UTF8, "application/json"));
        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task Lookup_WithBearer_ReturnsTargetInfo()
    {
        using var factory = new TestServerFactory();
        var client = factory.CreateClient();
        var viewer = await EnrollAsync(client);
        var host = await EnrollAsync(client);
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<YfgDeskDbContext>();
            var dev = await db.Devices.FirstAsync(d => d.DeviceId == host.DeviceId);
            dev.Online = true;
            dev.LastSeenIp = "5.5.5.5";
            dev.LastSeenPorts = "49153";
            dev.PublicIps = "5.5.5.5";
            dev.LastHeartbeatAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
        }

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", viewer.BearerToken);
        var body = JsonSerializer.Serialize(new { targetId = host.DeviceId });
        var resp = await client.PostAsync("/api/lookup",
            new StringContent(body, Encoding.UTF8, "application/json"));
        Assert.Equal(System.Net.HttpStatusCode.OK, resp.StatusCode);
        var json = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("5.5.5.5", json.GetProperty("ip").GetString());
        Assert.True(json.GetProperty("online").GetBoolean());
        Assert.Equal(host.DeviceId, json.GetProperty("deviceId").GetString());
    }

    [Fact]
    public async Task TurnCredentials_WithoutConfig_ReturnsDisabled()
    {
        using var factory = new TestServerFactory();
        var client = factory.CreateClient();
        var dev = await EnrollAsync(client);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", dev.BearerToken);
        var resp = await client.PostAsync("/api/turn-credentials",
            new StringContent("{}", Encoding.UTF8, "application/json"));
        Assert.Equal(System.Net.HttpStatusCode.OK, resp.StatusCode);
        var json = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(json.GetProperty("turnAvailable").GetBoolean());
    }

    [Fact]
    public async Task Reenroll_WithSameKey_RefreshesBearer()
    {
        using var factory = new TestServerFactory();
        var client = factory.CreateClient();
        var (spki, key) = CryptoTestUtils.GenerateP256KeyPair();

        var first = await EnrollAsync(client, spki, key);
        var second = await EnrollAsync(client, spki, key);
        Assert.Equal(first.DeviceId, second.DeviceId);
        Assert.NotEqual(first.BearerToken, second.BearerToken);
    }

    [Fact]
    public async Task Health_ReturnsOk()
    {
        using var factory = new TestServerFactory();
        var client = factory.CreateClient();
        var resp = await client.GetAsync("/api/health");
        Assert.Equal(System.Net.HttpStatusCode.OK, resp.StatusCode);
        var json = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("ok", json.GetProperty("status").GetString());
        Assert.Equal("2.0.0", json.GetProperty("version").GetString());
    }

    public static async Task<EnrollResult> EnrollAsync(HttpClient client, string? spki = null, ECDsa? key = null)
    {
        if (spki is null || key is null)
        {
            var (s, k) = CryptoTestUtils.GenerateP256KeyPair();
            spki = s; key = k;
        }
        var cbody = JsonSerializer.Serialize(new { publicKey = spki });
        var cresp = await client.PostAsync("/api/enroll/challenge",
            new StringContent(cbody, Encoding.UTF8, "application/json"));
        cresp.EnsureSuccessStatusCode();
        var cjson = await cresp.Content.ReadFromJsonAsync<JsonElement>();
        var challengeId = cjson.GetProperty("challengeId").GetString()!;
        var nonce = cjson.GetProperty("nonce").GetString()!;
        var sig = CryptoTestUtils.SignNonce(key!, nonce);
        var rbody = JsonSerializer.Serialize(new { challengeId, signature = sig });
        var rresp = await client.PostAsync("/api/enroll/register",
            new StringContent(rbody, Encoding.UTF8, "application/json"));
        rresp.EnsureSuccessStatusCode();
        var rjson = await rresp.Content.ReadFromJsonAsync<JsonElement>();
        return new EnrollResult(
            rjson.GetProperty("deviceId").GetString()!,
            rjson.GetProperty("bearerToken").GetString()!);
    }

    public sealed record EnrollResult(string DeviceId, string BearerToken);
}
