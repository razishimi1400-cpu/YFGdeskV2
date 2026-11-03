using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using YfgDesk.Server.Api;
using YfgDesk.Server.Crypto;
using YfgDesk.Server.Data;
using YfgDesk.Shared.Crypto;
using YfgDesk.Shared.Protocol;

namespace YfgDesk.Server.Signaling;

/// <summary>
/// WebSocket signaling hub. Accepts <c>GET /ws</c> upgrades, authenticates the bearer
/// token from the <c>Authorization</c> header, and dispatches JSON envelopes.
/// </summary>
public sealed class SignalingHub
{
    private readonly IServiceProvider _services;
    private readonly SignalingSessionStore _store;
    private readonly SignalingValidator _validator;
    private readonly RateLimiter _limiter;
    private readonly BearerTokenIssuer _bearer;
    private readonly SessionIdGenerator _sessionIds;
    private readonly TurnCredentialIssuer _turn;
    private readonly YfgDeskOptions _opts;
    private readonly ILogger<SignalingHub> _logger;
    private readonly AuditLog _audit;

    public SignalingHub(
        IServiceProvider services,
        SignalingSessionStore store,
        SignalingValidator validator,
        RateLimiter limiter,
        BearerTokenIssuer bearer,
        SessionIdGenerator sessionIds,
        TurnCredentialIssuer turn,
        YfgDeskOptions opts,
        ILogger<SignalingHub> logger,
        AuditLog audit)
    {
        _services = services;
        _store = store;
        _validator = validator;
        _limiter = limiter;
        _bearer = bearer;
        _sessionIds = sessionIds;
        _turn = turn;
        _opts = opts;
        _logger = logger;
        _audit = audit;
    }

    /// <summary>
    /// Entry point for the <c>/ws</c> route.
    /// </summary>
    public async Task HandleAsync(HttpContext ctx)
    {
        EndpointBase.ApplySecurityHeaders(ctx);

        if (EndpointBase.HasSecretInQuery(ctx.Request.Query))
        {
            await _audit.RecordAsync("error.malformed", null, null,
                EndpointBase.ClientIp(ctx), "secret in query string on /ws", ctx.RequestAborted);
            ctx.Response.StatusCode = 400;
            return;
        }
        if (!ctx.WebSockets.IsWebSocketRequest)
        {
            ctx.Response.StatusCode = 400;
            return;
        }

        // Auth: bearer in Authorization header only.
        if (!_bearer.TryExtract(ctx.Request.Headers.Authorization.ToString(), out var rawToken, out var digest))
        {
            await _audit.RecordAsync("ws.unauthorized", null, null,
                EndpointBase.ClientIp(ctx), "missing bearer on /ws upgrade", ctx.RequestAborted);
            ctx.Response.StatusCode = 401;
            return;
        }

        DeviceEntity? device;
        using (var scope = _services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<YfgDeskDbContext>();
            device = await db.Devices.AsNoTracking().FirstOrDefaultAsync(d => d.BearerDigest == digest, ctx.RequestAborted);
            if (device is null || (device.BearerExpiresAt is { } exp && exp < DateTime.UtcNow))
            {
                await _audit.RecordAsync("ws.unauthorized", null, null,
                    EndpointBase.ClientIp(ctx), "bearer not recognised", ctx.RequestAborted);
                ctx.Response.StatusCode = 401;
                return;
            }
        }

        var ip = EndpointBase.ClientIp(ctx);
        using var ws = await ctx.WebSockets.AcceptWebSocketAsync();
        var conn = new SignalingConnection
        {
            DeviceId = device.DeviceId,
            Socket = ws,
            Ip = ip,
            Role = device.Role,
            DisplayName = device.DisplayName ?? string.Empty,
        };

        // Replace any prior connection for this device.
        if (_store.TryGetConnection(device.DeviceId, out var prior))
        {
            try { if (prior.Socket.State == WebSocketState.Open)
                await prior.Socket.CloseAsync(WebSocketCloseStatus.PolicyViolation, "replaced", CancellationToken.None); }
            catch { /* ignore */ }
            _store.TryRemoveConnection(device.DeviceId, out _);
        }
        _store.TryAddConnection(conn);

        using (var scope = _services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<YfgDeskDbContext>();
            var d = await db.Devices.FirstAsync(x => x.DeviceId == device.DeviceId, ctx.RequestAborted);
            d.Online = true;
            d.LastHeartbeatAt = DateTime.UtcNow;
            d.LastSeenIp = ip;
            await db.SaveChangesAsync(ctx.RequestAborted);
        }

        await _audit.RecordAsync("ws.connect", device.DeviceId, null, ip, "WebSocket open", ctx.RequestAborted);

        // Send the initial `register` message.
        var register = new
        {
            version = ProtocolVersion.Current,
            messageId = "m_server_" + Guid.NewGuid().ToString("N"),
            timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            kind = MessageKinds.Register,
            payload = new
            {
                deviceId = device.DeviceId,
                heartbeatIntervalMs = _opts.HeartbeatIntervalSeconds * 1000,
            },
        };
        await SendJsonAsync(conn, register, ctx.RequestAborted);

        // Receive loop.
        var recvBuffer = new byte[16 * 1024];
        try
        {
            while (ws.State == WebSocketState.Open)
            {
                WebSocketReceiveResult result;
                using var ms = new MemoryStream();
                int totalRead = 0;
                do
                {
                    result = await ws.ReceiveAsync(new ArraySegment<byte>(recvBuffer), ctx.RequestAborted);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
                        break;
                    }
                    ms.Write(recvBuffer, 0, result.Count);
                    totalRead += result.Count;
                    if (totalRead > 32 * 1024)
                    {
                        await SendErrorAsync(conn, "message_too_large", "max 32 KiB per message", null, ctx.RequestAborted);
                        await ws.CloseAsync(WebSocketCloseStatus.MessageTooBig, "oversized", CancellationToken.None);
                        break;
                    }
                } while (!result.EndOfMessage);

                if (result.MessageType == WebSocketMessageType.Close) break;
                if (ms.Length == 0) continue;

                string text;
                try { text = Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length); }
                catch { await SendErrorAsync(conn, "invalid_encoding", "UTF-8 required", null, ctx.RequestAborted); continue; }

                await DispatchAsync(conn, text, ctx.RequestAborted);
            }
        }
        catch (OperationCanceledException) { /* shutdown */ }
        catch (WebSocketException wsex)
        {
            _logger.LogDebug(wsex, "WebSocket exception for device={DeviceId}", device.DeviceId);
        }
        finally
        {
            _store.TryRemoveConnection(device.DeviceId, out _);
            // Tear down sessions this device was a peer in.
            foreach (var s in _store.AllConnections) { /* no-op */ }
            foreach (var s in _store.AllSessions.Where(s => s.HostDeviceId == device.DeviceId || s.ViewerDeviceId == device.DeviceId).ToList())
            {
                if (s.State != SessionState.Ended && s.State != SessionState.Rejected && s.State != SessionState.TimedOut)
                {
                    s.State = SessionState.Ended;
                    s.EndedAt = DateTime.UtcNow;
                    s.EndReason = "peer_disconnect";
                    var peer = _store.PeerOf(s, device.DeviceId);
                    if (peer is not null && peer.Socket.State == WebSocketState.Open)
                    {
                        var endMsg = BuildEnvelope(MessageKinds.EndSession, new Payloads.EndSessionPayload("peer_disconnect"));
                        await SendJsonAsync(peer, endMsg, CancellationToken.None);
                    }
                }
            }
            // Mark device offline in DB.
            try
            {
                using var scope = _services.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<YfgDeskDbContext>();
                var d = await db.Devices.FirstAsync(x => x.DeviceId == device.DeviceId, CancellationToken.None);
                d.Online = false;
                await db.SaveChangesAsync(CancellationToken.None);
            }
            catch { /* best-effort */ }
            await _audit.RecordAsync("ws.disconnect", device.DeviceId, null, ip, "WebSocket closed", CancellationToken.None);
        }
    }

    private async Task DispatchAsync(SignalingConnection conn, string text, CancellationToken ct)
    {
        if (!_validator.TryParseEnvelope(text, out var env, out var parseError))
        {
            await _audit.RecordAsync("error.malformed", conn.DeviceId, null, conn.Ip, parseError, ct);
            await SendErrorAsync(conn, "malformed_envelope", parseError ?? "parse error", null, ct);
            return;
        }

        // Rate limit per device.
        if (!_limiter.TryConsume(RateLimitKeys.WsByDevice(conn.DeviceId), _opts.WsDeviceRateLimitPerMinute))
        {
            await _audit.RecordAsync("error.ratelimit", conn.DeviceId, env.SessionId, conn.Ip, "ws rate limit exceeded", ct);
            await SendErrorAsync(conn, "rate_limited", "ws rate limit exceeded", env.MessageId, ct);
            return;
        }

        switch (env.Kind)
        {
            case MessageKinds.HostReady:
                await OnHostReadyAsync(conn, env, ct);
                break;
            case MessageKinds.CreateSession:
                await OnCreateSessionAsync(conn, env, ct);
                break;
            case MessageKinds.ConsentDecision:
                await OnConsentDecisionAsync(conn, env, ct);
                break;
            case MessageKinds.Offer:
                await OnOfferAsync(conn, env, ct);
                break;
            case MessageKinds.Answer:
                await OnAnswerAsync(conn, env, ct);
                break;
            case MessageKinds.IceCandidate:
                await OnIceCandidateAsync(conn, env, ct);
                break;
            case MessageKinds.Heartbeat:
                await OnHeartbeatAsync(conn, env, ct);
                break;
            case MessageKinds.EndSession:
                await OnEndSessionAsync(conn, env, ct);
                break;
            default:
                await _audit.RecordAsync("error.malformed", conn.DeviceId, env.SessionId, conn.Ip,
                    $"unknown kind={env.Kind}", ct);
                await SendErrorAsync(conn, "unknown_kind", $"unknown kind: {env.Kind}", env.MessageId, ct);
                break;
        }
    }

    // ---- message handlers -------------------------------------------------

    private async Task OnHostReadyAsync(SignalingConnection conn, Envelope env, CancellationToken ct)
    {
        if (!_validator.TryGetHostReady(env, out var err))
        {
            await SendErrorAsync(conn, "malformed_payload", err ?? "invalid hostReady", env.MessageId, ct);
            return;
        }
        conn.Role = "HOST";
        // Update DB role.
        try
        {
            using var scope = _services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<YfgDeskDbContext>();
            var d = await db.Devices.FirstAsync(x => x.DeviceId == conn.DeviceId, ct);
            d.Role = "HOST";
            d.Online = true;
            d.LastHeartbeatAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex) { _logger.LogDebug(ex, "Failed to persist hostReady role"); }
        await _audit.RecordAsync("ws.host_ready", conn.DeviceId, null, conn.Ip, "host ready", ct);
        // Ack with empty 200-style envelope (no body needed).
        var ack = BuildEnvelope("ack", new { inReplyTo = env.MessageId });
        await SendJsonAsync(conn, ack, ct);
    }

    private async Task OnCreateSessionAsync(SignalingConnection conn, Envelope env, CancellationToken ct)
    {
        if (!_validator.TryGetCreateSession(env, out var create, out var err))
        {
            await SendErrorAsync(conn, "malformed_payload", err ?? "invalid createSession", env.MessageId, ct);
            return;
        }
        // The viewer becomes "VIEWER" role.
        conn.Role = "VIEWER";

        // Look up the host's connection.
        if (!_store.TryGetConnection(create.HostId, out var hostConn) || hostConn.Socket.State != WebSocketState.Open)
        {
            await _audit.RecordAsync("session.host_unavailable", conn.DeviceId, null, conn.Ip,
                $"host={create.HostId} not online", ct);
            await SendErrorAsync(conn, "host_unavailable", "host is not online", env.MessageId, ct);
            return;
        }

        // Allocate session id + record session.
        var sessionId = _sessionIds.Generate();
        var expiresAt = DateTime.UtcNow.AddSeconds(_opts.ConsentWaitSeconds);
        var vp = create.ToVideoPreference();
        var sanitizedDisplayName = Validators.SanitizeDisplayName(create.DisplayName);

        var state = _store.CreateSession(sessionId, create.HostId, conn.DeviceId,
            create.Permissions, vp, sanitizedDisplayName, expiresAt);

        // Persist to DB.
        using (var scope = _services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<YfgDeskDbContext>();
            // Update viewer role.
            var viewerDev = await db.Devices.FirstOrDefaultAsync(x => x.DeviceId == conn.DeviceId, ct);
            if (viewerDev is not null)
            {
                viewerDev.Role = "VIEWER";
                viewerDev.DisplayName = sanitizedDisplayName;
                viewerDev.Online = true;
                viewerDev.LastHeartbeatAt = DateTime.UtcNow;
            }
            db.Sessions.Add(new SessionEntity
            {
                SessionId = sessionId,
                HostDeviceId = create.HostId,
                ViewerDeviceId = conn.DeviceId,
                State = SessionState.Pending,
                RequestedPermissions = create.Permissions,
                VideoWidth = vp.Width,
                VideoHeight = vp.Height,
                VideoFramesPerSecond = vp.FramesPerSecond,
                VideoQualityMode = vp.QualityMode,
                ViewerDisplayName = sanitizedDisplayName,
                CreatedAt = state.CreatedAt,
                ExpiresAt = expiresAt,
            });
            try { await db.SaveChangesAsync(ct); }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to persist session entity");
            }
        }

        await _audit.RecordAsync("session.created", conn.DeviceId, sessionId, conn.Ip,
            $"host={create.HostId}", ct);

        // Replay tracking for createSession messageId.
        state.TryRecordMessageId(env.MessageId);

        // Send incomingRequest to the host.
        var incoming = BuildEnvelope(MessageKinds.IncomingRequest, new Payloads.IncomingRequestPayload(
            sessionId, conn.DeviceId, sanitizedDisplayName, create.Permissions, vp,
            new DateTimeOffset(expiresAt).ToUnixTimeSeconds()));
        await SendJsonAsync(hostConn, incoming, ct);
    }

    private async Task OnConsentDecisionAsync(SignalingConnection conn, Envelope env, CancellationToken ct)
    {
        if (!_validator.TryGetConsentDecision(env, out var decision, out var err))
        {
            await SendErrorAsync(conn, "malformed_payload", err ?? "invalid consentDecision", env.MessageId, ct);
            return;
        }
        if (env.SessionId is null || !_store.TryGetSession(env.SessionId, out var s))
        {
            await SendErrorAsync(conn, "unknown_session", "session not found", env.MessageId, ct);
            return;
        }
        // Role check: only the host can decide consent.
        if (conn.DeviceId != s.HostDeviceId)
        {
            await _audit.RecordAsync("error.forbidden", conn.DeviceId, s.SessionId, conn.Ip,
                "consentDecision by non-host", ct);
            await SendErrorAsync(conn, "forbidden", "only host can consent", env.MessageId, ct);
            return;
        }
        if (s.State != SessionState.Pending)
        {
            await SendErrorAsync(conn, "invalid_state", $"session in state {s.State}", env.MessageId, ct);
            return;
        }
        s.TryRecordMessageId(env.MessageId);

        if (!decision.Accept)
        {
            s.State = SessionState.Rejected;
            s.EndedAt = DateTime.UtcNow;
            s.EndReason = "host_rejected";
            await UpdateSessionStateDb(s.SessionId, SessionState.Rejected, "host_rejected", ct);
            await _audit.RecordAsync("session.rejected", conn.DeviceId, s.SessionId, conn.Ip, "host rejected", ct);
            // Notify the viewer.
            if (_store.TryGetConnection(s.ViewerDeviceId, out var viewer))
            {
                var end = BuildEnvelope(MessageKinds.EndSession, new Payloads.EndSessionPayload("host_rejected"));
                await SendJsonAsync(viewer, end, ct);
            }
            await SendJsonAsync(conn, BuildEnvelope("ack", new { inReplyTo = env.MessageId }), ct);
            return;
        }

        // Accept: transition to ACCEPTED and notify both peers with sessionStarted.
        s.State = SessionState.Accepted;
        s.GrantedPermissions = decision.GrantedPermissions;
        s.AcceptedAt = DateTime.UtcNow;
        await UpdateSessionStateDb(s.SessionId, SessionState.Accepted, null, ct, granted: decision.GrantedPermissions);
        await _audit.RecordAsync("session.accept", conn.DeviceId, s.SessionId, conn.Ip, "host accepted", ct);

        // Build iceServers (optional TURN).
        var iceServers = BuildIceServers(conn.DeviceId);
        var expiresAtEpoch = new DateTimeOffset(s.ExpiresAt > DateTime.UtcNow.AddSeconds(_opts.SessionMaxLifetimeSeconds)
            ? DateTime.UtcNow.AddSeconds(_opts.SessionMaxLifetimeSeconds) : s.ExpiresAt).ToUnixTimeSeconds();

        // Send sessionStarted to host.
        var hostStart = BuildEnvelope(MessageKinds.SessionStarted, new Payloads.SessionStartedPayload(
            s.SessionId, "host", s.GrantedPermissions, s.VideoPref!, iceServers, expiresAtEpoch));
        await SendJsonAsync(conn, hostStart, ct);

        // Send sessionStarted to viewer.
        if (_store.TryGetConnection(s.ViewerDeviceId, out var viewerConn))
        {
            var viewerStart = BuildEnvelope(MessageKinds.SessionStarted, new Payloads.SessionStartedPayload(
                s.SessionId, "viewer", s.GrantedPermissions, s.VideoPref!, iceServers, expiresAtEpoch));
            await SendJsonAsync(viewerConn, viewerStart, ct);
        }
    }

    private async Task OnOfferAsync(SignalingConnection conn, Envelope env, CancellationToken ct)
    {
        if (!_validator.TryGetSdp(env, out var sdp, out var err))
        {
            await _audit.RecordAsync("error.malformed", conn.DeviceId, env.SessionId, conn.Ip, err ?? "invalid sdp", ct);
            await SendErrorAsync(conn, "malformed_payload", err ?? "invalid sdp", env.MessageId, ct);
            return;
        }
        if (env.SessionId is null || !_store.TryGetSession(env.SessionId, out var s))
        {
            await SendErrorAsync(conn, "unknown_session", "session not found", env.MessageId, ct);
            return;
        }
        // Role check: offer only from Viewer.
        if (conn.DeviceId != s.ViewerDeviceId)
        {
            await _audit.RecordAsync("error.forbidden", conn.DeviceId, s.SessionId, conn.Ip,
                "offer by non-viewer", ct);
            await SendErrorAsync(conn, "forbidden", "only viewer can send offer", env.MessageId, ct);
            return;
        }
        if (s.State != SessionState.Accepted && s.State != SessionState.Active)
        {
            await SendErrorAsync(conn, "invalid_state", $"session in state {s.State}", env.MessageId, ct);
            return;
        }
        if (!s.TryRecordMessageId(env.MessageId))
        {
            await _audit.RecordAsync("error.replay", conn.DeviceId, s.SessionId, conn.Ip,
                "duplicate messageId in offer", ct);
            await SendErrorAsync(conn, "replay_detected", "messageId already seen", env.MessageId, ct);
            return;
        }
        // Relay to host with senderId set to the viewer.
        var relay = new Envelope
        {
            Version = ProtocolVersion.Current,
            MessageId = env.MessageId,
            SessionId = env.SessionId,
            SenderId = conn.DeviceId,
            Timestamp = env.Timestamp,
            Kind = MessageKinds.Offer,
            Payload = JsonSerializer.SerializeToElement(new Payloads.OfferPayload(sdp), JsonOptions.Default),
        };
        if (_store.TryGetConnection(s.HostDeviceId, out var host))
            await SendJsonAsync(host, relay, ct);
    }

    private async Task OnAnswerAsync(SignalingConnection conn, Envelope env, CancellationToken ct)
    {
        if (!_validator.TryGetSdp(env, out var sdp, out var err))
        {
            await _audit.RecordAsync("error.malformed", conn.DeviceId, env.SessionId, conn.Ip, err ?? "invalid sdp", ct);
            await SendErrorAsync(conn, "malformed_payload", err ?? "invalid sdp", env.MessageId, ct);
            return;
        }
        if (env.SessionId is null || !_store.TryGetSession(env.SessionId, out var s))
        {
            await SendErrorAsync(conn, "unknown_session", "session not found", env.MessageId, ct);
            return;
        }
        // Role check: answer only from Host.
        if (conn.DeviceId != s.HostDeviceId)
        {
            await _audit.RecordAsync("error.forbidden", conn.DeviceId, s.SessionId, conn.Ip,
                "answer by non-host", ct);
            await SendErrorAsync(conn, "forbidden", "only host can send answer", env.MessageId, ct);
            return;
        }
        if (s.State != SessionState.Accepted && s.State != SessionState.Active)
        {
            await SendErrorAsync(conn, "invalid_state", $"session in state {s.State}", env.MessageId, ct);
            return;
        }
        if (!s.TryRecordMessageId(env.MessageId))
        {
            await _audit.RecordAsync("error.replay", conn.DeviceId, s.SessionId, conn.Ip,
                "duplicate messageId in answer", ct);
            await SendErrorAsync(conn, "replay_detected", "messageId already seen", env.MessageId, ct);
            return;
        }
        // Transition to ACTIVE on first answer.
        if (s.State == SessionState.Accepted)
        {
            s.State = SessionState.Active;
            s.LastHeartbeatAt = DateTime.UtcNow;
            await UpdateSessionStateDb(s.SessionId, SessionState.Active, null, ct);
        }
        var relay = new Envelope
        {
            Version = ProtocolVersion.Current,
            MessageId = env.MessageId,
            SessionId = env.SessionId,
            SenderId = conn.DeviceId,
            Timestamp = env.Timestamp,
            Kind = MessageKinds.Answer,
            Payload = JsonSerializer.SerializeToElement(new Payloads.AnswerPayload(sdp), JsonOptions.Default),
        };
        if (_store.TryGetConnection(s.ViewerDeviceId, out var viewer))
            await SendJsonAsync(viewer, relay, ct);
    }

    private async Task OnIceCandidateAsync(SignalingConnection conn, Envelope env, CancellationToken ct)
    {
        if (!_validator.TryGetIceCandidate(env, out var cand, out var err))
        {
            await _audit.RecordAsync("error.malformed", conn.DeviceId, env.SessionId, conn.Ip,
                err ?? "invalid candidate", ct);
            await SendErrorAsync(conn, "malformed_payload", err ?? "invalid candidate", env.MessageId, ct);
            return;
        }
        if (env.SessionId is null || !_store.TryGetSession(env.SessionId, out var s))
        {
            await SendErrorAsync(conn, "unknown_session", "session not found", env.MessageId, ct);
            return;
        }
        if (conn.DeviceId != s.HostDeviceId && conn.DeviceId != s.ViewerDeviceId)
        {
            await SendErrorAsync(conn, "forbidden", "not a session peer", env.MessageId, ct);
            return;
        }
        if (s.State != SessionState.Active && s.State != SessionState.Accepted)
        {
            await SendErrorAsync(conn, "invalid_state", $"session in state {s.State}", env.MessageId, ct);
            return;
        }
        if (!s.TryRecordMessageId(env.MessageId))
        {
            await _audit.RecordAsync("error.replay", conn.DeviceId, s.SessionId, conn.Ip,
                "duplicate messageId in iceCandidate", ct);
            await SendErrorAsync(conn, "replay_detected", "messageId already seen", env.MessageId, ct);
            return;
        }
        s.LastHeartbeatAt = DateTime.UtcNow;
        var peer = _store.PeerOf(s, conn.DeviceId);
        if (peer is null) return;
        var relay = new Envelope
        {
            Version = ProtocolVersion.Current,
            MessageId = env.MessageId,
            SessionId = env.SessionId,
            SenderId = conn.DeviceId,
            Timestamp = env.Timestamp,
            Kind = MessageKinds.IceCandidate,
            Payload = JsonSerializer.SerializeToElement(cand, JsonOptions.Default),
        };
        await SendJsonAsync(peer, relay, ct);
    }

    private async Task OnHeartbeatAsync(SignalingConnection conn, Envelope env, CancellationToken ct)
    {
        conn.LastHeartbeatAt = DateTime.UtcNow;
        // Update DB last heartbeat.
        try
        {
            using var scope = _services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<YfgDeskDbContext>();
            var d = await db.Devices.FirstAsync(x => x.DeviceId == conn.DeviceId, ct);
            d.Online = true;
            d.LastHeartbeatAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex) { _logger.LogDebug(ex, "Failed to update heartbeat"); }
        if (env.SessionId is not null && _store.TryGetSession(env.SessionId, out var s))
        {
            s.LastHeartbeatAt = DateTime.UtcNow;
        }
        var ack = BuildEnvelope(MessageKinds.Heartbeat, new Payloads.HeartbeatPayload(
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
        // Replace the messageId with one prefixed with "m_server_" to make acks easy to identify.
        ack = new Envelope
        {
            Version = ack.Version,
            MessageId = "m_server_" + Guid.NewGuid().ToString("N"),
            SessionId = ack.SessionId,
            Timestamp = ack.Timestamp,
            Kind = ack.Kind,
            Payload = ack.Payload,
        };
        await SendJsonAsync(conn, ack, ct);
    }

    private async Task OnEndSessionAsync(SignalingConnection conn, Envelope env, CancellationToken ct)
    {
        if (!_validator.TryGetEndSession(env, out var end, out var err))
        {
            await SendErrorAsync(conn, "malformed_payload", err ?? "invalid endSession", env.MessageId, ct);
            return;
        }
        if (env.SessionId is null || !_store.TryGetSession(env.SessionId, out var s))
        {
            await SendErrorAsync(conn, "unknown_session", "session not found", env.MessageId, ct);
            return;
        }
        if (conn.DeviceId != s.HostDeviceId && conn.DeviceId != s.ViewerDeviceId)
        {
            await SendErrorAsync(conn, "forbidden", "not a session peer", env.MessageId, ct);
            return;
        }
        var reason = string.IsNullOrEmpty(end.Reason) ? "ended" : end.Reason;
        if (reason.Length > 64) reason = reason.Substring(0, 64);
        s.State = SessionState.Ended;
        s.EndedAt = DateTime.UtcNow;
        s.EndReason = reason;
        await UpdateSessionStateDb(s.SessionId, SessionState.Ended, reason, ct);
        await _audit.RecordAsync("session.ended", conn.DeviceId, s.SessionId, conn.Ip,
            $"reason={reason}", ct);
        // Notify both peers.
        var endMsg = BuildEnvelope(MessageKinds.EndSession, new Payloads.EndSessionPayload(reason));
        if (_store.TryGetConnection(s.HostDeviceId, out var host) && host.Socket.State == WebSocketState.Open)
            await SendJsonAsync(host, endMsg, ct);
        if (_store.TryGetConnection(s.ViewerDeviceId, out var viewer) && viewer.Socket.State == WebSocketState.Open
            && viewer.DeviceId != conn.DeviceId)
            await SendJsonAsync(viewer, endMsg, ct);
        _store.TryRemoveSession(s.SessionId, out _);
    }

    // ---- helpers ----------------------------------------------------------

    private IceServer[] BuildIceServers(string deviceId)
    {
        if (!_opts.Turn.IsConfigured) return Array.Empty<IceServer>();
        var bundle = _turn.Issue(deviceId);
        if (bundle is null) return Array.Empty<IceServer>();
        var ice = _turn.ToIceServer(bundle);
        return ice is null ? Array.Empty<IceServer>() : new[] { ice };
    }

    private async Task UpdateSessionStateDb(string sessionId, SessionState state, string? endReason,
        CancellationToken ct, int? granted = null)
    {
        try
        {
            using var scope = _services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<YfgDeskDbContext>();
            var row = await db.Sessions.FirstOrDefaultAsync(x => x.SessionId == sessionId, ct);
            if (row is null) return;
            row.State = state;
            if (granted is not null) row.GrantedPermissions = granted;
            if (state == SessionState.Accepted) row.AcceptedAt = DateTime.UtcNow;
            if (state == SessionState.Ended || state == SessionState.Rejected || state == SessionState.TimedOut)
            {
                row.EndedAt = DateTime.UtcNow;
                row.EndReason = endReason;
            }
            row.LastHeartbeatAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to persist session state");
        }
    }

    private static Envelope BuildEnvelope(string kind, object payload)
    {
        return new Envelope
        {
            Version = ProtocolVersion.Current,
            MessageId = "m_server_" + Guid.NewGuid().ToString("N"),
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Kind = kind,
            Payload = JsonSerializer.SerializeToElement(payload, JsonOptions.Default),
        };
    }

    private async Task SendJsonAsync(SignalingConnection conn, object body, CancellationToken ct)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(body, JsonOptions.Default);
        await conn.SendLock.WaitAsync(ct);
        try
        {
            if (conn.Socket.State == WebSocketState.Open)
                await conn.Socket.SendAsync(json, WebSocketMessageType.Text, endOfMessage: true, ct);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Send failed on device={DeviceId}", conn.DeviceId);
        }
        finally { conn.SendLock.Release(); }
    }

    private async Task SendErrorAsync(SignalingConnection conn, string code, string detail,
        string? inReplyTo, CancellationToken ct)
    {
        var env = BuildEnvelope(MessageKinds.Error, new Payloads.ErrorPayload(code, detail, inReplyTo));
        await SendJsonAsync(conn, env, ct);
    }
}
