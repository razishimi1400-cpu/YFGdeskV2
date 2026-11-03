using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using YfgDesk.Server.Crypto;
using YfgDesk.Server.Data;
using YfgDesk.Shared.Crypto;
using YfgDesk.Shared.Protocol;

namespace YfgDesk.Server.Api;

/// <summary>
/// <c>POST /api/enroll/register</c> — exchange a signed challenge for a deviceId + bearer token.
/// Request body: <c>{ challengeId, signature }</c>
/// Response: <c>{ deviceId, bearerToken, expiresAt }</c>
/// </summary>
public static class EnrollRegisterEndpoint
{
    public static async Task HandleAsync(
        HttpContext ctx,
        YfgDeskDbContext db,
        ChallengeStore challenges,
        RateLimiter limiter,
        DeviceIdGenerator deviceIdGen,
        BearerTokenIssuer bearerIssuer,
        AuditLog audit,
        YfgDeskOptions opts,
        CancellationToken ct = default)
    {
        EndpointBase.ApplySecurityHeaders(ctx);
        var ip = EndpointBase.ClientIp(ctx);

        if (EndpointBase.HasSecretInQuery(ctx.Request.Query))
        {
            await audit.RecordAsync("error.malformed", null, null, ip, "secret in query string", ct);
            await EndpointBase.WriteErrorAsync(ctx, 400, "malformed_request", "secrets must not appear in query string", null, ct);
            return;
        }

        if (!limiter.TryConsume(RateLimitKeys.EnrollByIp(ip), opts.EnrollIpRateLimitPerMinute))
        {
            await audit.RecordAsync("error.ratelimit", null, null, ip, "enroll/register rate limit exceeded", ct);
            await EndpointBase.WriteErrorAsync(ctx, 429, "rate_limited", "enroll register rate limit exceeded", null, ct);
            return;
        }

        var bodyText = await EndpointBase.ReadBodyAsync(ctx, opts.PerEndpointMaxBodyBytes, ct);
        if (bodyText is null)
        {
            await audit.RecordAsync("error.malformed", null, null, ip, "oversized or invalid body", ct);
            await EndpointBase.WriteErrorAsync(ctx, 400, "malformed_request", "body too large or invalid", null, ct);
            return;
        }

        EnrollRegisterRequest? req;
        try { req = JsonSerializer.Deserialize<EnrollRegisterRequest>(bodyText, JsonOptions.Default); }
        catch
        {
            await audit.RecordAsync("error.malformed", null, null, ip, "invalid JSON", ct);
            await EndpointBase.WriteErrorAsync(ctx, 400, "malformed_request", "invalid JSON body", null, ct);
            return;
        }
        if (req is null || string.IsNullOrEmpty(req.ChallengeId) || string.IsNullOrEmpty(req.Signature))
        {
            await audit.RecordAsync("error.malformed", null, null, ip, "missing challengeId or signature", ct);
            await EndpointBase.WriteErrorAsync(ctx, 400, "malformed_request", "challengeId and signature required", null, ct);
            return;
        }

        var challenge = challenges.TryConsume(req.ChallengeId);
        if (challenge is null)
        {
            await audit.RecordAsync("enroll.challenge_missing", null, null, ip,
                "challenge not found or expired", ct);
            await EndpointBase.WriteErrorAsync(ctx, 404, "challenge_not_found", "challenge missing or expired", null, ct);
            return;
        }

        if (challenge.IssuedForIp != ip)
        {
            await audit.RecordAsync("error.forbidden", null, null, ip,
                "challenge IP mismatch", ct);
            await EndpointBase.WriteErrorAsync(ctx, 403, "forbidden", "challenge IP mismatch", null, ct);
            return;
        }

        // Verify the ECDSA signature over the nonce.
        using var verifier = new EcdsaVerifier();
        if (!verifier.TryImportSpkiBase64(challenge.PublicKeySpki))
        {
            await audit.RecordAsync("error.malformed", null, null, ip, "stored SPKI invalid", ct);
            await EndpointBase.WriteErrorAsync(ctx, 500, "internal_error", "stored SPKI invalid", null, ct);
            return;
        }

        if (!verifier.VerifySignature(challenge.Nonce, req.Signature))
        {
            await audit.RecordAsync("enroll.signature_invalid", null, null, ip,
                "signature verification failed", ct);
            await EndpointBase.WriteErrorAsync(ctx, 401, "signature_invalid", "signature verification failed", null, ct);
            return;
        }

        // Idempotency: if a device already exists with this public key digest, re-issue its bearer.
        var existing = await db.Devices.FirstOrDefaultAsync(d => d.PublicKeyDigest == challenge.PublicKeyDigest, ct);
        DeviceEntity device;
        if (existing is not null)
        {
            // Re-issue bearer (single active bearer per device).
            var (raw, digest) = bearerIssuer.Issue();
            existing.BearerDigest = digest;
            existing.BearerExpiresAt = DateTime.UtcNow.AddSeconds(opts.BearerTtlSeconds);
            existing.LastSeenIp = ip;
            await db.SaveChangesAsync(ct);
            device = existing;
            await audit.RecordAsync("enroll.reissued", device.DeviceId, null, ip, "device re-enrolled", ct);
            var resp = new EnrollRegisterResponse(device.DeviceId, raw, existing.BearerExpiresAt!.Value);
            await EndpointBase.WriteJsonAsync(ctx, 200, resp, ct);
            return;
        }

        // Allocate a fresh deviceId (collision-checked).
        string deviceId;
        try { deviceId = await deviceIdGen.GenerateUniqueAsync(db, ct); }
        catch
        {
            await audit.RecordAsync("error.busy", null, null, ip, "could not allocate deviceId", ct);
            await EndpointBase.WriteErrorAsync(ctx, 503, "service_busy", "could not allocate deviceId", null, ct);
            return;
        }

        var (rawToken, bearerDigest) = bearerIssuer.Issue();
        var bearerExpiry = DateTime.UtcNow.AddSeconds(opts.BearerTtlSeconds);

        device = new DeviceEntity
        {
            DeviceId = deviceId,
            PublicKeySpki = challenge.PublicKeySpki,
            PublicKeyDigest = challenge.PublicKeyDigest,
            BearerDigest = bearerDigest,
            BearerExpiresAt = bearerExpiry,
            Role = "VIEWER",
            LastSeenIp = ip,
            CreatedAt = DateTime.UtcNow,
        };
        db.Devices.Add(device);
        await db.SaveChangesAsync(ct);

        await audit.RecordAsync("enroll.issued", device.DeviceId, null, ip,
            "new device enrolled", ct);

        var response = new EnrollRegisterResponse(deviceId, rawToken, bearerExpiry);
        await EndpointBase.WriteJsonAsync(ctx, 200, response, ct);
    }

    public sealed record EnrollRegisterRequest(string ChallengeId, string Signature);

    public sealed record EnrollRegisterResponse(string DeviceId, string BearerToken, DateTime ExpiresAt);
}
