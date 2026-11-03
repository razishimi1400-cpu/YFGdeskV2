using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using YfgDesk.Server.Crypto;
using YfgDesk.Server.Data;
using YfgDesk.Shared.Crypto;
using YfgDesk.Shared.Protocol;

namespace YfgDesk.Server.Api;

/// <summary>
/// <c>POST /api/lookup</c> — discovery lookup for a target device's IP/port/online state.
/// Bearer-auth required.
/// Body: <c>{ targetId: "123456789" }</c>
/// Response: <c>{ ip, port, online, lastSeen, publicIps, listenPorts }</c>
/// </summary>
public static class LookupEndpoint
{
    public static async Task HandleAsync(
        HttpContext ctx,
        YfgDeskDbContext db,
        BearerTokenIssuer bearerIssuer,
        AuditLog audit,
        YfgDeskOptions opts,
        CancellationToken ct = default)
    {
        EndpointBase.ApplySecurityHeaders(ctx);

        var device = await EndpointBase.ResolveBearerAsync(ctx, db, bearerIssuer, audit, ct);
        if (device is null) return;

        var ip = EndpointBase.ClientIp(ctx);

        var bodyText = await EndpointBase.ReadBodyAsync(ctx, opts.PerEndpointMaxBodyBytes, ct);
        if (bodyText is null)
        {
            await audit.RecordAsync("error.malformed", device.DeviceId, null, ip, "oversized or invalid body", ct);
            await EndpointBase.WriteErrorAsync(ctx, 400, "malformed_request", "body too large or invalid", null, ct);
            return;
        }

        LookupRequest? req;
        try { req = JsonSerializer.Deserialize<LookupRequest>(bodyText, JsonOptions.Default); }
        catch
        {
            await audit.RecordAsync("error.malformed", device.DeviceId, null, ip, "invalid JSON", ct);
            await EndpointBase.WriteErrorAsync(ctx, 400, "malformed_request", "invalid JSON body", null, ct);
            return;
        }
        if (req is null || !Validators.IsValidDeviceId(req.TargetId))
        {
            await audit.RecordAsync("error.malformed", device.DeviceId, null, ip, "invalid targetId", ct);
            await EndpointBase.WriteErrorAsync(ctx, 400, "malformed_request", "valid targetId required", null, ct);
            return;
        }

        var target = await db.Devices.AsNoTracking().FirstOrDefaultAsync(d => d.DeviceId == req.TargetId, ct);
        if (target is null)
        {
            await audit.RecordAsync("lookup.miss", device.DeviceId, null, ip, $"target={req.TargetId}", ct);
            await EndpointBase.WriteErrorAsync(ctx, 404, "not_found", "target device not found", null, ct);
            return;
        }

        await audit.RecordAsync("lookup.hit", device.DeviceId, null, ip, $"target={req.TargetId}", ct);

        // Pick the first listen port as the "port" field for backwards-compat with v1 clients.
        int? port = null;
        if (target.ListenPortsList.Length > 0 &&
            int.TryParse(target.ListenPortsList[0], out var p))
        {
            port = p;
        }

        var resp = new LookupResponse(
            target.LastSeenIp ?? string.Empty,
            port,
            target.Online,
            target.LastHeartbeatAt ?? DateTime.MinValue,
            target.PublicIpsList,
            target.ListenPortsList,
            target.DeviceId,
            target.DisplayName ?? string.Empty);

        await EndpointBase.WriteJsonAsync(ctx, 200, resp, ct);
    }

    public sealed record LookupRequest(string TargetId);

    public sealed record LookupResponse(
        string Ip,
        int? Port,
        bool Online,
        DateTime LastSeen,
        string[] PublicIps,
        string[] ListenPorts,
        string DeviceId,
        string DisplayName);
}
