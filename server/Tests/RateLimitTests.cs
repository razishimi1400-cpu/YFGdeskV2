using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using System.Net.WebSockets;
using YfgDesk.Server.Data;
using YfgDesk.Shared.Protocol;

namespace YfgDesk.Server.Tests;

/// <summary>
/// Rate-limit tests for /api/enroll/* (per-IP) and WebSocket messages (per-device).
/// Uses a custom factory with strict limits (4/min) so the thresholds are exercised quickly.
/// </summary>
public sealed class RateLimitTests
{
    private static readonly JsonSerializerOptions Json = JsonOptions.Default;

    private sealed class StrictRateFactory : TestServerFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<YfgDeskOptions>();
                services.AddSingleton(new YfgDeskOptions
                {
                    DatabaseConnectionString = DatabaseConnectionString,
                    ChallengeTtlSeconds = 5,
                    ConsentWaitSeconds = 5,
                    SessionIdleSeconds = 2,
                    SweepIntervalSeconds = 1,
                    WsKeepAliveSeconds = 5,
                    HeartbeatIntervalSeconds = 5,
                    ReplayWindowSize = 8,
                    TimestampSkewSeconds = 5,
                    EnrollIpRateLimitPerMinute = 4,
                    WsDeviceRateLimitPerMinute = 4,
                    BearerTtlSeconds = 3600,
                    SessionMaxLifetimeSeconds = 60,
                });
            });
        }
    }

    [Fact]
    public async Task EnrollChallenge_PerIpLimit_Returns429AfterThreshold()
    {
        using var factory = new StrictRateFactory();
        var client = factory.CreateClient();
        var (spki, _) = CryptoTestUtils.GenerateP256KeyPair();
        var body = JsonSerializer.Serialize(new { publicKey = spki }, Json);

        // 4 should succeed (StrictRateFactory sets EnrollIpRateLimitPerMinute=4).
        for (var i = 0; i < 4; i++)
        {
            var resp = await client.PostAsync("/api/enroll/challenge",
                new StringContent(body, Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        }

        // 5th must be rate-limited.
        var over = await client.PostAsync("/api/enroll/challenge",
            new StringContent(body, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.TooManyRequests, over.StatusCode);
    }

    [Fact]
    public async Task EnrollRegister_PerIpLimit_Returns429AfterThreshold()
    {
        using var factory = new StrictRateFactory();
        var client = factory.CreateClient();
        var body = "{\"challengeId\":\"c_doesnotexist\",\"signature\":\"AAAA\"}";

        for (var i = 0; i < 4; i++)
        {
            var resp = await client.PostAsync("/api/enroll/register",
                new StringContent(body, Encoding.UTF8, "application/json"));
            // We expect 404 (challenge not found) — but still consumed by rate limit.
            Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
        }

        var over = await client.PostAsync("/api/enroll/register",
            new StringContent(body, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.TooManyRequests, over.StatusCode);
    }

    [Fact]
    public async Task Ws_PerDeviceLimit_ReturnsRateLimitedErrorAfterThreshold()
    {
        using var factory = new StrictRateFactory();
        var enroll = await EnrollFlowTests.EnrollAsync(factory.CreateClient());
        var wsc = factory.Server.CreateWebSocketClient();
        wsc.ConfigureRequest = req => req.Headers["Authorization"] = $"Bearer {enroll.BearerToken}";
        using var ws = await wsc.ConnectAsync(new Uri("http://localhost/ws"), default);

        // Drain the register message.
        _ = await ReceiveJsonAsync(ws);

        // 4 heartbeats should be allowed.
        for (var i = 0; i < 4; i++)
        {
            var hb = CryptoTestUtils.BuildEnvelope(MessageKinds.Heartbeat, $"m_hb_{i}", null, new { });
            await SendJsonAsync(ws, hb);
            var ack = await ReceiveJsonAsync(ws);
            Assert.Equal(MessageKinds.Heartbeat, ack.GetProperty("kind").GetString());
        }

        // 5th must be rate-limited.
        var hb5 = CryptoTestUtils.BuildEnvelope(MessageKinds.Heartbeat, "m_hb_5", null, new { });
        await SendJsonAsync(ws, hb5);
        var err = await ReceiveJsonAsync(ws);
        Assert.Equal(MessageKinds.Error, err.GetProperty("kind").GetString());
        Assert.Equal("rate_limited", err.GetProperty("payload").GetProperty("code").GetString());
    }

    [Fact]
    public async Task SecretInQuery_Returns400()
    {
        using var factory = new TestServerFactory();
        var client = factory.CreateClient();
        // Add `?token=...` to /api/health — even though health is public, the global
        // middleware rejects any secret-bearing query string.
        var resp = await client.GetAsync("/api/health?token=secret");
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    // ---- helpers --------------------------------------------------------

    private static async Task<JsonElement> ReceiveJsonAsync(WebSocket ws)
    {
        var buf = new byte[16 * 1024];
        using var ms = new MemoryStream();
        WebSocketReceiveResult r;
        do
        {
            r = await ws.ReceiveAsync(new ArraySegment<byte>(buf), default);
            if (r.MessageType == WebSocketMessageType.Close)
                throw new InvalidOperationException("WebSocket closed by peer");
            ms.Write(buf, 0, r.Count);
        } while (!r.EndOfMessage);
        var text = Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length);
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    private static Task SendJsonAsync(WebSocket ws, string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        return ws.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, default);
    }
}
