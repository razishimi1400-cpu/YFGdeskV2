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
/// <c>POST /api/turn-credentials</c> — short-lived TURN credentials.
/// Bearer-auth required.
/// Response: <c>{ username, credential, urls, expiresAt }</c> or
/// <c>{ turnAvailable: false, urls: [] }</c> if TURN is not configured.
/// </summary>
public static class TurnCredentialsEndpoint
{
    public static async Task HandleAsync(
        HttpContext ctx,
        YfgDeskDbContext db,
        BearerTokenIssuer bearerIssuer,
        TurnCredentialIssuer turnIssuer,
        AuditLog audit,
        YfgDeskOptions opts,
        CancellationToken ct = default)
    {
        EndpointBase.ApplySecurityHeaders(ctx);

        var device = await EndpointBase.ResolveBearerAsync(ctx, db, bearerIssuer, audit, ct);
        if (device is null) return;

        var ip = EndpointBase.ClientIp(ctx);

        // Cap request body (no real payload needed, but consume it for hygiene).
        var bodyText = await EndpointBase.ReadBodyAsync(ctx, opts.PerEndpointMaxBodyBytes, ct);
        if (bodyText is null)
        {
            await audit.RecordAsync("error.malformed", device.DeviceId, null, ip, "oversized body", ct);
            await EndpointBase.WriteErrorAsync(ctx, 400, "malformed_request", "body too large", null, ct);
            return;
        }

        if (!opts.Turn.IsConfigured)
        {
            await audit.RecordAsync("turn.disabled", device.DeviceId, null, ip, "TURN not configured", ct);
            var disabled = new
            {
                turnAvailable = false,
                urls = Array.Empty<string>(),
            };
            await EndpointBase.WriteJsonAsync(ctx, 200, disabled, ct);
            return;
        }

        var bundle = turnIssuer.Issue(device.DeviceId);
        if (bundle is null)
        {
            await audit.RecordAsync("turn.error", device.DeviceId, null, ip, "issuance failed", ct);
            await EndpointBase.WriteErrorAsync(ctx, 503, "turn_error", "could not issue TURN credentials", null, ct);
            return;
        }

        await audit.RecordAsync("turn.issued", device.DeviceId, null, ip,
            $"ttl={opts.Turn.TtlSeconds}", ct);

        var resp = new
        {
            turnAvailable = true,
            username = bundle.Username,
            credential = bundle.Credential,
            urls = bundle.Urls,
            expiresAt = bundle.ExpiresAt,
        };
        await EndpointBase.WriteJsonAsync(ctx, 200, resp, ct);
    }
}
