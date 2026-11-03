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
/// <c>POST /api/enroll/challenge</c> — issue an enrollment challenge.
/// Request body: <c>{ publicKey: "&lt;spki base64&gt;" }</c>
/// Response: <c>{ challengeId, nonce, expiresAt }</c>
/// </summary>
public static class EnrollChallengeEndpoint
{
    public static async Task HandleAsync(
        HttpContext ctx,
        YfgDeskDbContext db,
        ChallengeStore challenges,
        RateLimiter limiter,
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
            await audit.RecordAsync("error.ratelimit", null, null, ip, "enroll/challenge rate limit exceeded", ct);
            await EndpointBase.WriteErrorAsync(ctx, 429, "rate_limited", "enroll challenge rate limit exceeded", null, ct);
            return;
        }

        var bodyText = await EndpointBase.ReadBodyAsync(ctx, opts.PerEndpointMaxBodyBytes, ct);
        if (bodyText is null)
        {
            await audit.RecordAsync("error.malformed", null, null, ip, "oversized or invalid body", ct);
            await EndpointBase.WriteErrorAsync(ctx, 400, "malformed_request", "body too large or invalid", null, ct);
            return;
        }

        EnrollChallengeRequest? req;
        try { req = JsonSerializer.Deserialize<EnrollChallengeRequest>(bodyText, JsonOptions.Default); }
        catch
        {
            await audit.RecordAsync("error.malformed", null, null, ip, "invalid JSON", ct);
            await EndpointBase.WriteErrorAsync(ctx, 400, "malformed_request", "invalid JSON body", null, ct);
            return;
        }
        if (req is null || string.IsNullOrWhiteSpace(req.PublicKey))
        {
            await audit.RecordAsync("error.malformed", null, null, ip, "missing publicKey", ct);
            await EndpointBase.WriteErrorAsync(ctx, 400, "malformed_request", "publicKey required", null, ct);
            return;
        }

        // Import the SPKI public key via EcdsaVerifier so malformed keys are rejected early.
        using var verifier = new EcdsaVerifier();
        if (!verifier.TryImportSpkiBase64(req.PublicKey))
        {
            await audit.RecordAsync("error.malformed", null, null, ip, "invalid SPKI", ct);
            await EndpointBase.WriteErrorAsync(ctx, 400, "malformed_request", "invalid SPKI public key", null, ct);
            return;
        }

        var spkiDigest = Hasher.Sha256Hex(req.PublicKey.Trim());

        // Issue nonce + challenge ID.
        Span<byte> nonceBytes = stackalloc byte[32];
        RandomNumberGenerator.Fill(nonceBytes);
        var nonceHex = Convert.ToHexString(nonceBytes.ToArray()).ToLowerInvariant();
        // Hex nonce is hard to read on the wire — but the client just signs it. Use plain hex.

        var challengeId = "c_" + Guid.NewGuid().ToString("N");
        var expiresAt = DateTime.UtcNow.AddSeconds(opts.ChallengeTtlSeconds);

        var challenge = new ChallengeStore.Challenge(
            challengeId, nonceHex, req.PublicKey.Trim(), spkiDigest, ip, expiresAt);

        if (!challenges.TryAdd(challenge))
        {
            await audit.RecordAsync("error.busy", null, null, ip, "challenge store full", ct);
            await EndpointBase.WriteErrorAsync(ctx, 503, "service_busy", "challenge store saturated", null, ct);
            return;
        }

        await audit.RecordAsync("enroll.challenge_issued", null, null, ip,
            $"digest={spkiDigest.Substring(0, 8)}", ct);

        var resp = new EnrollChallengeResponse(challengeId, nonceHex, expiresAt);
        await EndpointBase.WriteJsonAsync(ctx, 200, resp, ct);
    }

    public sealed record EnrollChallengeRequest(string PublicKey);

    public sealed record EnrollChallengeResponse(string ChallengeId, string Nonce, DateTime ExpiresAt);
}
