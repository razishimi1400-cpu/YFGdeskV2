using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using YfgDesk.Server.Data;
using YfgDesk.Shared.Protocol;

namespace YfgDesk.Server.Tests;

/// <summary>
/// Session lifecycle tests: PENDING → ACCEPTED → ACTIVE → ENDED, idle timeout,
/// host-reject path.
/// </summary>
public sealed class SessionLifecycleTests
{
    private static readonly JsonSerializerOptions Json = JsonOptions.Default;

    [Fact]
    public async Task FullHappyPath_PendingAcceptedActiveEnded()
    {
        using var factory = new TestServerFactory();
        var hostEnroll = await EnrollFlowTests.EnrollAsync(factory.CreateClient());
        var viewerEnroll = await EnrollFlowTests.EnrollAsync(factory.CreateClient());

        var hostWs = await ConnectAsync(factory, hostEnroll.BearerToken);
        var viewerWs = await ConnectAsync(factory, viewerEnroll.BearerToken);
        _ = await ReceiveJsonAsync(hostWs);
        _ = await ReceiveJsonAsync(viewerWs);

        // Host ready.
        await SendJsonAsync(hostWs, CryptoTestUtils.BuildEnvelope(MessageKinds.HostReady, "m_hr_1", null, new { }));
        _ = await ReceiveJsonAsync(hostWs);

        // Viewer creates session.
        var cs = CryptoTestUtils.BuildEnvelope(MessageKinds.CreateSession, "m_cs_1", null, new
        {
            hostId = hostEnroll.DeviceId,
            permissions = 1,
            displayName = "Viewer",
            videoWidth = 1280,
            videoHeight = 720,
            videoFps = 30,
            videoQualityMode = "Standard",
        });
        await SendJsonAsync(viewerWs, cs);

        var incoming = await ReceiveJsonAsync(hostWs);
        Assert.Equal(MessageKinds.IncomingRequest, incoming.GetProperty("kind").GetString());
        var sessionId = incoming.GetProperty("payload").GetProperty("sessionId").GetString();

        // Verify DB row created with state=Pending.
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<YfgDeskDbContext>();
            var sess = await db.Sessions.FirstAsync(s => s.SessionId == sessionId);
            Assert.Equal(SessionState.Pending, sess.State);
        }

        // Host accepts.
        await SendJsonAsync(hostWs, CryptoTestUtils.BuildEnvelope(MessageKinds.ConsentDecision, "m_cd_1", sessionId, new
        {
            accept = true,
            grantedPermissions = 1,
        }));
        var hostStart = await ReceiveJsonAsync(hostWs);
        var viewerStart = await ReceiveJsonAsync(viewerWs);
        Assert.Equal(MessageKinds.SessionStarted, hostStart.GetProperty("kind").GetString());
        Assert.Equal(MessageKinds.SessionStarted, viewerStart.GetProperty("kind").GetString());
        Assert.Equal("host", hostStart.GetProperty("payload").GetProperty("role").GetString());
        Assert.Equal("viewer", viewerStart.GetProperty("payload").GetProperty("role").GetString());

        // Viewer sends offer; relayed to host.
        var offer = CryptoTestUtils.BuildEnvelope(MessageKinds.Offer, "m_offer_1", sessionId, new
        {
            sdp = "v=0\r\nm=video 9 UDP/TLS/RTP/SAVPF 96\r\na=fingerprint:sha-256 AA:BB:CC\r\na=mid:0\r\n",
        });
        await SendJsonAsync(viewerWs, offer);
        var relayedOffer = await ReceiveJsonAsync(hostWs);
        Assert.Equal(MessageKinds.Offer, relayedOffer.GetProperty("kind").GetString());

        // Host sends answer; relayed to viewer, session becomes ACTIVE.
        var answer = CryptoTestUtils.BuildEnvelope(MessageKinds.Answer, "m_answer_1", sessionId, new
        {
            sdp = "v=0\r\nm=video 9 UDP/TLS/RTP/SAVPF 96\r\na=fingerprint:sha-256 DD:EE:FF\r\na=mid:0\r\n",
        });
        await SendJsonAsync(hostWs, answer);
        var relayedAnswer = await ReceiveJsonAsync(viewerWs);
        Assert.Equal(MessageKinds.Answer, relayedAnswer.GetProperty("kind").GetString());

        // ICE candidates bidirectional.
        var iceV = CryptoTestUtils.BuildEnvelope(MessageKinds.IceCandidate, "m_ice_v_1", sessionId, new
        {
            candidate = "candidate:1 1 udp 100 192.168.1.10 49153 typ host",
            sdpMid = "0",
            sdpMLineIndex = 0,
        });
        await SendJsonAsync(viewerWs, iceV);
        var iceRelayedToHost = await ReceiveJsonAsync(hostWs);
        Assert.Equal(MessageKinds.IceCandidate, iceRelayedToHost.GetProperty("kind").GetString());

        var iceH = CryptoTestUtils.BuildEnvelope(MessageKinds.IceCandidate, "m_ice_h_1", sessionId, new
        {
            candidate = "candidate:2 1 udp 100 192.168.1.20 49154 typ host",
            sdpMid = "0",
            sdpMLineIndex = 0,
        });
        await SendJsonAsync(hostWs, iceH);
        var iceRelayedToViewer = await ReceiveJsonAsync(viewerWs);
        Assert.Equal(MessageKinds.IceCandidate, iceRelayedToViewer.GetProperty("kind").GetString());

        // Either peer ends the session.
        var end = CryptoTestUtils.BuildEnvelope(MessageKinds.EndSession, "m_end_1", sessionId, new
        {
            reason = "user_ended",
        });
        await SendJsonAsync(hostWs, end);
        var endToHost = await ReceiveJsonAsync(hostWs);
        var endToViewer = await ReceiveJsonAsync(viewerWs);
        Assert.Equal(MessageKinds.EndSession, endToHost.GetProperty("kind").GetString());
        Assert.Equal(MessageKinds.EndSession, endToViewer.GetProperty("kind").GetString());

        // Verify DB row updated.
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<YfgDeskDbContext>();
            var sess = await db.Sessions.FirstAsync(s => s.SessionId == sessionId);
            Assert.Equal(SessionState.Ended, sess.State);
            Assert.Equal("user_ended", sess.EndReason);
            Assert.NotNull(sess.EndedAt);
        }

        await hostWs.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", default);
        await viewerWs.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", default);
    }

    [Fact]
    public async Task HostReject_MarksSessionRejectedAndNotifiesViewer()
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
            displayName = "Viewer",
            videoWidth = 1280,
            videoHeight = 720,
            videoFps = 30,
            videoQualityMode = "Standard",
        });
        await SendJsonAsync(viewerWs, cs);

        var incoming = await ReceiveJsonAsync(hostWs);
        var sessionId = incoming.GetProperty("payload").GetProperty("sessionId").GetString();

        // Host rejects.
        await SendJsonAsync(hostWs, CryptoTestUtils.BuildEnvelope(MessageKinds.ConsentDecision, "m_cd_1", sessionId, new
        {
            accept = false,
            grantedPermissions = 0,
        }));
        var ack = await ReceiveJsonAsync(hostWs);
        Assert.Equal("ack", ack.GetProperty("kind").GetString());

        var endToViewer = await ReceiveJsonAsync(viewerWs);
        Assert.Equal(MessageKinds.EndSession, endToViewer.GetProperty("kind").GetString());
        Assert.Equal("host_rejected", endToViewer.GetProperty("payload").GetProperty("reason").GetString());

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<YfgDeskDbContext>();
        var sess = await db.Sessions.FirstAsync(s => s.SessionId == sessionId);
        Assert.Equal(SessionState.Rejected, sess.State);

        await hostWs.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", default);
        await viewerWs.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", default);
    }

    [Fact]
    public async Task IdleTimeout_SweepsSessionAfter2sOfInactivity()
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
            displayName = "Viewer",
            videoWidth = 1280,
            videoHeight = 720,
            videoFps = 30,
            videoQualityMode = "Standard",
        });
        await SendJsonAsync(viewerWs, cs);

        var incoming = await ReceiveJsonAsync(hostWs);
        var sessionId = incoming.GetProperty("payload").GetProperty("sessionId").GetString();

        // Host accepts, both peers get sessionStarted. Then we go silent.
        await SendJsonAsync(hostWs, CryptoTestUtils.BuildEnvelope(MessageKinds.ConsentDecision, "m_cd_1", sessionId, new
        {
            accept = true,
            grantedPermissions = 1,
        }));
        _ = await ReceiveJsonAsync(hostWs);
        _ = await ReceiveJsonAsync(viewerWs);

        // Wait long enough for the sweep service to tear down the idle session.
        // (TestServerFactory sets SessionIdleSeconds=2, SweepIntervalSeconds=1.)
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var sawEnd = false;
        while (!cts.IsCancellationRequested && !sawEnd)
        {
            var hostTask = ReceiveJsonAsync(hostWs);
            var viewerTask = ReceiveJsonAsync(viewerWs);
            var completed = await Task.WhenAny(hostTask, viewerTask);
            var msg = completed == hostTask ? await hostTask : await viewerTask;
            if (msg.GetProperty("kind").GetString() == MessageKinds.EndSession)
            {
                sawEnd = true;
                break;
            }
        }
        Assert.True(sawEnd, "expected the sweep service to deliver an endSession message after idle timeout");

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<YfgDeskDbContext>();
        var sess = await db.Sessions.FirstAsync(s => s.SessionId == sessionId);
        Assert.True(sess.State == SessionState.Ended || sess.State == SessionState.TimedOut,
            $"state was {sess.State}");
        Assert.Equal("idle_timeout", sess.EndReason);

        await hostWs.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", default);
        await viewerWs.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", default);
    }

    // ---- helpers --------------------------------------------------------

    private static async Task<WebSocket> ConnectAsync(TestServerFactory factory, string bearer)
    {
        var wsc = factory.Server.CreateWebSocketClient();
        wsc.ConfigureRequest = req => req.Headers["Authorization"] = $"Bearer {bearer}";
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
}
