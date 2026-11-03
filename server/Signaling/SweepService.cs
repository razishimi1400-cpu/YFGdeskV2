using Microsoft.EntityFrameworkCore;
using System.Net.WebSockets;
using YfgDesk.Server.Api;
using YfgDesk.Server.Data;

namespace YfgDesk.Server.Signaling;

/// <summary>
/// Background service that periodically sweeps:
///   - expired in-memory challenges
///   - expired / idle signaling sessions
///   - offline-mark any device whose last heartbeat is older than 90s
/// </summary>
public sealed class SweepService : BackgroundService
{
    private readonly IServiceProvider _services;
    private readonly SignalingSessionStore _store;
    private readonly ChallengeStore _challenges;
    private readonly RateLimiter _limiter;
    private readonly YfgDeskOptions _opts;
    private readonly ILogger<SweepService> _logger;
    private readonly AuditLog _audit;

    public SweepService(
        IServiceProvider services,
        SignalingSessionStore store,
        ChallengeStore challenges,
        RateLimiter limiter,
        YfgDeskOptions opts,
        ILogger<SweepService> logger,
        AuditLog audit)
    {
        _services = services;
        _store = store;
        _challenges = challenges;
        _limiter = limiter;
        _opts = opts;
        _logger = logger;
        _audit = audit;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("SweepService started, interval={Interval}s", _opts.SweepIntervalSeconds);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(_opts.SweepIntervalSeconds), stoppingToken);
                await SweepOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Sweep iteration failed");
            }
        }
    }

    private async Task SweepOnceAsync(CancellationToken ct)
    {
        // 1) In-memory challenge prune.
        _challenges.PruneExpired();
        // 2) RateLimiter bucket trim.
        _limiter.Trim();
        // 3) Signaling session sweep.
        var expiredSessions = _store.Sweep();
        foreach (var s in expiredSessions)
        {
            // Update DB row FIRST so callers polling the DB see the new state immediately.
            try
            {
                using var scope = _services.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<YfgDeskDbContext>();
                var row = await db.Sessions.FirstOrDefaultAsync(x => x.SessionId == s.SessionId, ct);
                if (row is not null)
                {
                    row.State = s.State;
                    row.EndedAt = s.EndedAt ?? DateTime.UtcNow;
                    row.EndReason = s.EndReason;
                    await db.SaveChangesAsync(ct);
                }
            }
            catch (Exception ex) { _logger.LogDebug(ex, "Failed to persist swept session"); }
            await _audit.RecordAsync("session.expired", null, s.SessionId, null,
                $"reason={s.EndReason}", ct);
            // Then notify both peers, if connected.
            await NotifyPeersAsync(s, ct);
        }
        // 4) Offline-mark stale devices in DB (no heartbeat for 90s).
        try
        {
            using var scope = _services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<YfgDeskDbContext>();
            var cutoff = DateTime.UtcNow.AddSeconds(-90);
            var stale = await db.Devices.Where(d => d.Online && d.LastHeartbeatAt != null && d.LastHeartbeatAt < cutoff)
                .ToListAsync(ct);
            foreach (var d in stale)
            {
                d.Online = false;
            }
            if (stale.Count > 0)
                await db.SaveChangesAsync(ct);
        }
        catch (Exception ex) { _logger.LogDebug(ex, "Failed to offline-mark stale devices"); }
    }

    private async Task NotifyPeersAsync(SignalingSessionState s, CancellationToken ct)
    {
        var endMsg = new
        {
            version = YfgDesk.Shared.Protocol.ProtocolVersion.Current,
            messageId = "m_server_" + Guid.NewGuid().ToString("N"),
            timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            kind = YfgDesk.Shared.Protocol.MessageKinds.EndSession,
            sessionId = s.SessionId,
            payload = new { reason = s.EndReason ?? "expired" },
        };
        var bytes = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(endMsg, YfgDesk.Shared.Protocol.JsonOptions.Default);
        foreach (var deviceId in new[] { s.HostDeviceId, s.ViewerDeviceId })
        {
            if (_store.TryGetConnection(deviceId, out var conn) && conn.Socket.State == WebSocketState.Open)
            {
                await conn.SendLock.WaitAsync(ct);
                try { await conn.Socket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, ct); }
                catch { /* ignore */ }
                finally { conn.SendLock.Release(); }
            }
        }
    }
}
