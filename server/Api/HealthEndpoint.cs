using System.Text.Json;
using Microsoft.AspNetCore.Http;
using YfgDesk.Shared.Protocol;

namespace YfgDesk.Server.Api;

/// <summary>
/// <c>GET /api/health</c> — public health-check endpoint.
/// </summary>
public static class HealthEndpoint
{
    public const string Version = "2.0.0";

    public static Task HandleAsync(HttpContext ctx)
    {
        EndpointBase.ApplySecurityHeaders(ctx);
        var body = new
        {
            status = "ok",
            version = Version,
            time = DateTime.UtcNow,
        };
        return EndpointBase.WriteJsonAsync(ctx, StatusCodes.Status200OK, body, ctx.RequestAborted);
    }
}
