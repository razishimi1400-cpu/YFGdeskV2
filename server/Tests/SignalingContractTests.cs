using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.TestHost;
using YfgDesk.Shared.Protocol;

namespace YfgDesk.Server.Tests;

/// <summary>
/// Contract-level tests for the WebSocket signaling layer: envelope casing,
/// anti-replay window, timestamp skew enforcement, role enforcement.
/// </summary>
public sealed class SignalingContractTests
{
    private static readonly JsonSerializerOptions Json = JsonOptions.Default;

    [Fact]
    public async Task WsUpgrade_RequiresBearer()
    {
        using var factory = new TestServerFactory();
        var client = factory.CreateClient();
        var wsc = factory.Server.CreateWebSocketClient();
        // No Authorization header → connect must fail.
        await Assert.ThrowsAnyAsync<InvalidOperationException>(async () =>
            await wsc.ConnectAsync(new Uri("http://localhost/ws"), default));
    }

    [Fact]
    public async Task WsUpgrade_SendsRegisterEnvelopeInCamelCase()
    {
        using var factory = new TestServerFactory();
        var ws = await ConnectAsync(factory);
        var register = await ReceiveJsonAsync(ws);

        Assert.Equal(ProtocolVersion.Current, register.GetProperty("version").GetInt32());
        Assert.Equal(MessageKinds.Register, register.GetProperty("kind").GetString());
        // camelCase fields must be present (not PascalCase).
        Assert.True(register.TryGetProperty("messageId", out _), "expected camelCase messageId");
        Assert.True(register.TryGetProperty("payload", out var payload));
        Assert.True(payload.TryGetProperty("deviceId", out _));
        Assert.True(payload.TryGetProperty("heartbeatIntervalMs", out _));
        await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", default);
    }

    [Fact]
    public async Task WsUpgrade_RejectsSecretInQueryString()
    {
        using var factory = new TestServerFactory();
        var enroll = await EnrollFlowTests.EnrollAsync(factory.CreateClient());
        var wsc = factory.Server.CreateWebSocketClient();
        wsc.ConfigureRequest = req => req.Headers["Authorization"] = $"Bearer {enroll.BearerToken}";
        // Bearer in query string must be rejected even when Authorization is set.
        await Assert.ThrowsAnyAsync<InvalidOperationException>(async () =>
            await wsc.ConnectAsync(new Uri("http://localhost/ws?token=foo"), default));
    }

    [Fact]
    public async Task Envelope_TimestampSkewRejected()
    {
        using var factory = new TestServerFactory();
        var ws = await ConnectAsync(factory);
        // Drain the register message first.
        _ = await ReceiveJsonAsync(ws);

        // Send a heartbeat with a timestamp far in the past.
        var stale = CryptoTestUtils.BuildEnvelope(
            MessageKinds.Heartbeat,
            "m_stale_1",
            sessionId: null,
            payload: new { },
            timestampOverride: DateTimeOffset.UtcNow.AddSeconds(-3600).ToUnixTimeMilliseconds());
        await SendJsonAsync(ws, stale);

        var err = await ReceiveJsonAsync(ws);
        Assert.Equal(MessageKinds.Error, err.GetProperty("kind").GetString());
        Assert.Equal("malformed_envelope", err.GetProperty("payload").GetProperty("code").GetString());
        await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", default);
    }

    [Fact]
    public async Task Envelope_MalformedJsonRejected()
    {
        using var factory = new TestServerFactory();
        var ws = await ConnectAsync(factory);
        _ = await ReceiveJsonAsync(ws);

        await SendTextAsync(ws, "not-json");
        var err = await ReceiveJsonAsync(ws);
        Assert.Equal(MessageKinds.Error, err.GetProperty("kind").GetString());
        Assert.Equal("malformed_envelope", err.GetProperty("payload").GetProperty("code").GetString());
        await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", default);
    }

    [Fact]
    public async Task Heartbeat_AcksWithServerTime()
    {
        using var factory = new TestServerFactory();
        var ws = await ConnectAsync(factory);
        _ = await ReceiveJsonAsync(ws);

        var hb = CryptoTestUtils.BuildEnvelope(MessageKinds.Heartbeat, "m_hb_1", null, new { });
        await SendJsonAsync(ws, hb);

        var ack = await ReceiveJsonAsync(ws);
        Assert.Equal(MessageKinds.Heartbeat, ack.GetProperty("kind").GetString());
        Assert.True(ack.GetProperty("payload").TryGetProperty("serverTime", out _));
        await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", default);
    }

    [Fact]
    public async Task HostReady_Acknowledged()
    {
        using var factory = new TestServerFactory();
        var ws = await ConnectAsync(factory);
        _ = await ReceiveJsonAsync(ws);

        var hr = CryptoTestUtils.BuildEnvelope(MessageKinds.HostReady, "m_hr_1", null, new { });
        await SendJsonAsync(ws, hr);

        var ack = await ReceiveJsonAsync(ws);
        Assert.Equal("ack", ack.GetProperty("kind").GetString());
        await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", default);
    }

    [Fact]
    public async Task Offer_RejectedForHostRole()
    {
        // The host cannot send an offer; only the viewer can.
        using var factory = new TestServerFactory();
        var hostEnroll = await EnrollFlowTests.EnrollAsync(factory.CreateClient());
        var viewerEnroll = await EnrollFlowTests.EnrollAsync(factory.CreateClient());

        var hostWs = await ConnectAsync(factory, hostEnroll.BearerToken);
        _ = await ReceiveJsonAsync(hostWs);
        await SendJsonAsync(hostWs, CryptoTestUtils.BuildEnvelope(MessageKinds.HostReady, "m_hr_1", null, new { }));
        _ = await ReceiveJsonAsync(hostWs); // ack

        var viewerWs = await ConnectAsync(factory, viewerEnroll.BearerToken);
        _ = await ReceiveJsonAsync(viewerWs);

        // Create a session.
        var cs = CryptoTestUtils.BuildEnvelope(MessageKinds.CreateSession, "m_cs_1", null, new
        {
            hostId = hostEnroll.DeviceId,
            permissions = 1,
            displayName = "Viewer A",
            videoWidth = 1280,
            videoHeight = 720,
            videoFps = 30,
            videoQualityMode = "Standard",
        });
        await SendJsonAsync(viewerWs, cs);

        // Host should receive incomingRequest.
        var incoming = await ReceiveJsonAsync(hostWs);
        Assert.Equal(MessageKinds.IncomingRequest, incoming.GetProperty("kind").GetString());
        var sessionId = incoming.GetProperty("payload").GetProperty("sessionId").GetString();

        // Host accepts.
        var accept = CryptoTestUtils.BuildEnvelope(MessageKinds.ConsentDecision, "m_cd_1", sessionId, new
        {
            accept = true,
            grantedPermissions = 1,
        });
        await SendJsonAsync(hostWs, accept);

        // Both should receive sessionStarted.
        var hostStart = await ReceiveJsonAsync(hostWs);
        var viewerStart = await ReceiveJsonAsync(viewerWs);
        Assert.Equal(MessageKinds.SessionStarted, hostStart.GetProperty("kind").GetString());
        Assert.Equal(MessageKinds.SessionStarted, viewerStart.GetProperty("kind").GetString());

        // Now host tries to send an offer — must be rejected with forbidden.
        var badOffer = CryptoTestUtils.BuildEnvelope(MessageKinds.Offer, "m_offer_bad", sessionId, new
        {
            sdp = "v=0\r\nm=video 9 UDP/TLS/RTP/SAVPF 96\r\na=fingerprint:sha-256 AA:BB:CC\r\na=mid:0\r\n",
        });
        await SendJsonAsync(hostWs, badOffer);
        var err = await ReceiveJsonAsync(hostWs);
        Assert.Equal(MessageKinds.Error, err.GetProperty("kind").GetString());
        Assert.Equal("forbidden", err.GetProperty("payload").GetProperty("code").GetString());

        await hostWs.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", default);
        await viewerWs.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", default);
    }

    [Fact]
    public async Task AntiReplay_DuplicateMessageIdRejected()
    {
        using var factory = new TestServerFactory();
        var hostEnroll = await EnrollFlowTests.EnrollAsync(factory.CreateClient());
        var viewerEnroll = await EnrollFlowTests.EnrollAsync(factory.CreateClient());

        var hostWs = await ConnectAsync(factory, hostEnroll.BearerToken);
        var viewerWs = await ConnectAsync(factory, viewerEnroll.BearerToken);
        _ = await ReceiveJsonAsync(hostWs);
        _ = await ReceiveJsonAsync(viewerWs);
        await SendJsonAsync(hostWs, CryptoTestUtils.BuildEnvelope(MessageKinds.HostReady, "m_hr_1", null, new { }));
        _ = await ReceiveJsonAsync(hostWs);

        var cs = CryptoTestUtils.BuildEnvelope(MessageKinds.CreateSession, "m_cs_1", null, new
        {
            hostId = hostEnroll.DeviceId,
            permissions = 1,
            displayName = "Viewer A",
            videoWidth = 1280,
            videoHeight = 720,
            videoFps = 30,
            videoQualityMode = "Standard",
        });
        await SendJsonAsync(viewerWs, cs);

        var incoming = await ReceiveJsonAsync(hostWs);
        var sessionId = incoming.GetProperty("payload").GetProperty("sessionId").GetString();

        await SendJsonAsync(hostWs, CryptoTestUtils.BuildEnvelope(MessageKinds.ConsentDecision, "m_cd_1", sessionId, new
        {
            accept = true,
            grantedPermissions = 1,
        }));
        _ = await ReceiveJsonAsync(hostWs);
        _ = await ReceiveJsonAsync(viewerWs);

        // First offer with messageId m_offer_1 — should be accepted (relayed to host).
        var offer = CryptoTestUtils.BuildEnvelope(MessageKinds.Offer, "m_offer_1", sessionId, new
        {
            sdp = "v=0\r\nm=video 9 UDP/TLS/RTP/SAVPF 96\r\na=fingerprint:sha-256 AA:BB:CC\r\na=mid:0\r\n",
        });
        await SendJsonAsync(viewerWs, offer);
        var relayed = await ReceiveJsonAsync(hostWs);
        Assert.Equal(MessageKinds.Offer, relayed.GetProperty("kind").GetString());

        // Replay the same messageId — should be rejected.
        await SendJsonAsync(viewerWs, offer);
        var err = await ReceiveJsonAsync(viewerWs);
        Assert.Equal(MessageKinds.Error, err.GetProperty("kind").GetString());
        Assert.Equal("replay_detected", err.GetProperty("payload").GetProperty("code").GetString());

        await hostWs.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", default);
        await viewerWs.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", default);
    }

    // ---- helpers --------------------------------------------------------

    private static async Task<WebSocket> ConnectAsync(TestServerFactory factory, string? bearer = null)
    {
        var token = bearer ?? (await EnrollFlowTests.EnrollAsync(factory.CreateClient())).BearerToken;
        var wsc = factory.Server.CreateWebSocketClient();
        wsc.ConfigureRequest = req => req.Headers["Authorization"] = $"Bearer {token}";
        return await wsc.ConnectAsync(new Uri("http://localhost/ws"), default);
    }

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

    private static Task SendTextAsync(WebSocket ws, string text) => SendJsonAsync(ws, text);
}
