using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using YfgDesk.Server.Crypto;
using YfgDesk.Server.Data;
using YfgDesk.Shared.Protocol;

namespace YfgDesk.Server.Api;

/// <summary>
/// Shared helpers for minimal-API endpoints: JSON write, error response,
/// body-size cap, query-string secret detection.
/// </summary>
public abstract class EndpointBase
{
    protected static readonly JsonSerializerOptions JsonOpts = JsonOptions.Default;

    /// <summary>
    /// True when the request URL contains any secret-bearing query keys.
    /// Reject such requests with 400.
    /// </summary>
    public static bool HasSecretInQuery(IQueryCollection query)
    {
        if (query is null || query.Count == 0) return false;
        foreach (var key in query.Keys)
        {
            if (string.IsNullOrEmpty(key)) continue;
            var lower = key.ToLowerInvariant();
            if (lower is "token" or "bearer" or "password" or "secret" or "auth" or "access_token")
                return true;
        }
        return false;
    }

    /// <summary>
    /// Write a JSON response with the standard security headers.
    /// </summary>
    public static async Task WriteJsonAsync(HttpContext ctx, int status, object body, CancellationToken ct = default)
    {
        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = "application/json; charset=utf-8";
        ApplySecurityHeaders(ctx);
        await JsonSerializer.SerializeAsync(ctx.Response.Body, body, JsonOpts, ct);
    }

    /// <summary>
    /// Write a JSON error envelope.
    /// </summary>
    public static Task WriteErrorAsync(HttpContext ctx, int status, string code, string detail,
        string? inReplyTo = null, CancellationToken ct = default)
    {
        var payload = new
        {
            error = new { code, detail, inReplyTo },
        };
        return WriteJsonAsync(ctx, status, payload, ct);
    }

    /// <summary>
    /// Apply mandatory security headers (called for every response).
    /// </summary>
    public static void ApplySecurityHeaders(HttpContext ctx)
    {
        var h = ctx.Response.Headers;
        h["X-Content-Type-Options"] = "nosniff";
        h["X-Frame-Options"] = "DENY";
        h["Referrer-Policy"] = "no-referrer";
        h["Cache-Control"] = "no-store";
        if (ctx.Request.IsHttps)
        {
            h["Strict-Transport-Security"] = "max-age=31536000; includeSubDomains";
        }
    }

    /// <summary>
    /// Read the request body and reject if larger than <paramref name="maxBytes"/>.
    /// Returns null if the body is too large or invalid UTF-8.
    /// </summary>
    public static async Task<string?> ReadBodyAsync(HttpContext ctx, int maxBytes, CancellationToken ct = default)
    {
        // Content-Length check first (cheap).
        var cl = ctx.Request.ContentLength ?? 0;
        if (cl > maxBytes) return null;
        using var ms = new MemoryStream();
        var read = 0;
        var buffer = new byte[8192];
        int n;
        try
        {
            while ((n = await ctx.Request.Body.ReadAsync(buffer, ct)) > 0)
            {
                read += n;
                if (read > maxBytes) return null;
                ms.Write(buffer, 0, n);
            }
        }
        catch
        {
            return null;
        }
        try
        {
            return Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Extract the client IP, honoring X-Forwarded-For when reverse-proxy is trusted.
    /// </summary>
    public static string ClientIp(HttpContext ctx)
    {
        var ip = ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        return ip;
    }

    /// <summary>
    /// Validate that the Authorization header carries a bearer token. Sets the
    /// resolved <see cref="DeviceEntity"/> on <c>ctx.Items["device"]</c> on success.
    /// Returns the device or null.
    /// </summary>
    public static async Task<DeviceEntity?> ResolveBearerAsync(
        HttpContext ctx,
        YfgDeskDbContext db,
        BearerTokenIssuer issuer,
        AuditLog audit,
        CancellationToken ct = default)
    {
        if (HasSecretInQuery(ctx.Request.Query))
        {
            await audit.RecordAsync("error.forbidden", null, null, ClientIp(ctx),
                "secret in query string", ct);
            ctx.Response.StatusCode = (int)HttpStatusCode.BadRequest;
            ApplySecurityHeaders(ctx);
            return null;
        }
        var header = ctx.Request.Headers.Authorization.ToString();
        if (!issuer.TryExtract(header, out var raw, out var digest))
        {
            await audit.RecordAsync("error.forbidden", null, null, ClientIp(ctx),
                "missing or malformed bearer", ct);
            ctx.Response.StatusCode = (int)HttpStatusCode.Unauthorized;
            ApplySecurityHeaders(ctx);
            return null;
        }
        var device = await db.Devices.AsNoTracking()
            .FirstOrDefaultAsync(d => d.BearerDigest == digest, ct);
        if (device is null)
        {
            await audit.RecordAsync("error.forbidden", null, null, ClientIp(ctx),
                "bearer not recognised", ct);
            ctx.Response.StatusCode = (int)HttpStatusCode.Unauthorized;
            ApplySecurityHeaders(ctx);
            return null;
        }
        if (device.BearerExpiresAt is { } exp && exp < DateTime.UtcNow)
        {
            await audit.RecordAsync("error.forbidden", device.DeviceId, null, ClientIp(ctx),
                "bearer expired", ct);
            ctx.Response.StatusCode = (int)HttpStatusCode.Unauthorized;
            ApplySecurityHeaders(ctx);
            return null;
        }
        ctx.Items["device"] = device;
        ctx.Items["bearer_digest"] = digest;
        // We deliberately do NOT log the bearer itself.
        return device;
    }
}
