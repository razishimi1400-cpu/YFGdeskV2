using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using YfgDesk.Server.Api;
using YfgDesk.Server.Crypto;
using YfgDesk.Server.Data;
using YfgDesk.Server.Signaling;
using YfgDesk.Shared.Protocol;

namespace YfgDesk.Server;

public partial class Program
{
    public static void Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);

        // --- Options ------------------------------------------------------
        var opts = LoadOptions(builder.Configuration);
        builder.Services.AddSingleton(opts);

        // --- Database -----------------------------------------------------
        builder.Services.AddDbContext<YfgDeskDbContext>(o =>
        {
            o.UseSqlite(opts.DatabaseConnectionString);
        });

        // --- Crypto / helpers ---------------------------------------------
        builder.Services.AddSingleton<DeviceIdGenerator>();
        builder.Services.AddSingleton<SessionIdGenerator>();
        builder.Services.AddSingleton<BearerTokenIssuer>();
        builder.Services.AddSingleton<TurnCredentialIssuer>();
        builder.Services.AddSingleton<ChallengeStore>(_ => new ChallengeStore(opts.MaxPendingChallenges, opts.ChallengeTtlSeconds));
        builder.Services.AddSingleton<RateLimiter>();
        builder.Services.AddSingleton<AuditLog>();
        builder.Services.AddSingleton<SignalingValidator>();
        builder.Services.AddSingleton<SignalingSessionStore>();
        builder.Services.AddSingleton<SignalingHub>();

        // --- Background sweep --------------------------------------------
        builder.Services.AddHostedService<SweepService>();

        // --- HTTP / Kestrel ----------------------------------------------
        builder.WebHost.ConfigureKestrel(ko =>
        {
            ko.Limits.MaxRequestBodySize = opts.MaxRequestBodyBytes;
        });

        builder.Services.Configure<ForwardedHeadersOptions>(o =>
        {
            o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
            o.KnownIPNetworks.Clear();
            o.KnownProxies.Clear();
        });

        var app = builder.Build();

        // Apply ForwardedHeaders BEFORE routing.
        app.UseForwardedHeaders();

        // Apply mandatory security headers to every response.
        app.Use(async (ctx, next) =>
        {
            ctx.Response.OnStarting(() =>
            {
                EndpointBase.ApplySecurityHeaders(ctx);
                return Task.CompletedTask;
            });
            await next();
        });

        // Cap body size globally per request.
        app.Use(async (ctx, next) =>
        {
            // Reject obvious secret-bearing query strings on every endpoint.
            if (EndpointBase.HasSecretInQuery(ctx.Request.Query))
            {
                ctx.Response.StatusCode = 400;
                EndpointBase.ApplySecurityHeaders(ctx);
                await ctx.Response.WriteAsync("secrets must not appear in query string");
                return;
            }
            await next();
        });

        app.UseWebSockets(new WebSocketOptions
        {
            KeepAliveInterval = TimeSpan.FromSeconds(opts.WsKeepAliveSeconds),
        });

        // --- Ensure DB created -------------------------------------------
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<YfgDeskDbContext>();
            db.Database.EnsureCreated();
        }

        // --- REST routes --------------------------------------------------
        app.MapGet("/api/health", HealthEndpoint.HandleAsync);

        app.MapPost("/api/enroll/challenge", async ctx =>
        {
            using var scope = ctx.RequestServices.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<YfgDeskDbContext>();
            var store = scope.ServiceProvider.GetRequiredService<ChallengeStore>();
            var limiter = ctx.RequestServices.GetRequiredService<RateLimiter>();
            var audit = ctx.RequestServices.GetRequiredService<AuditLog>();
            var cfg = ctx.RequestServices.GetRequiredService<YfgDeskOptions>();
            await EnrollChallengeEndpoint.HandleAsync(ctx, db, store, limiter, audit, cfg, ctx.RequestAborted);
        });

        app.MapPost("/api/enroll/register", async ctx =>
        {
            using var scope = ctx.RequestServices.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<YfgDeskDbContext>();
            var store = scope.ServiceProvider.GetRequiredService<ChallengeStore>();
            var limiter = ctx.RequestServices.GetRequiredService<RateLimiter>();
            var idGen = ctx.RequestServices.GetRequiredService<DeviceIdGenerator>();
            var bearer = ctx.RequestServices.GetRequiredService<BearerTokenIssuer>();
            var audit = ctx.RequestServices.GetRequiredService<AuditLog>();
            var cfg = ctx.RequestServices.GetRequiredService<YfgDeskOptions>();
            await EnrollRegisterEndpoint.HandleAsync(ctx, db, store, limiter, idGen, bearer, audit, cfg, ctx.RequestAborted);
        });

        app.MapPost("/api/lookup", async ctx =>
        {
            using var scope = ctx.RequestServices.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<YfgDeskDbContext>();
            var bearer = ctx.RequestServices.GetRequiredService<BearerTokenIssuer>();
            var audit = ctx.RequestServices.GetRequiredService<AuditLog>();
            var cfg = ctx.RequestServices.GetRequiredService<YfgDeskOptions>();
            await LookupEndpoint.HandleAsync(ctx, db, bearer, audit, cfg, ctx.RequestAborted);
        });

        app.MapPost("/api/turn-credentials", async ctx =>
        {
            using var scope = ctx.RequestServices.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<YfgDeskDbContext>();
            var bearer = ctx.RequestServices.GetRequiredService<BearerTokenIssuer>();
            var turn = ctx.RequestServices.GetRequiredService<TurnCredentialIssuer>();
            var audit = ctx.RequestServices.GetRequiredService<AuditLog>();
            var cfg = ctx.RequestServices.GetRequiredService<YfgDeskOptions>();
            await TurnCredentialsEndpoint.HandleAsync(ctx, db, bearer, turn, audit, cfg, ctx.RequestAborted);
        });

        // --- WebSocket route ---------------------------------------------
        app.MapGet("/ws", async ctx =>
        {
            var hub = ctx.RequestServices.GetRequiredService<SignalingHub>();
            await hub.HandleAsync(ctx);
        });

        app.Run();
    }

    private static YfgDeskOptions LoadOptions(IConfiguration config)
    {
        var opts = new YfgDeskOptions();
        config.GetSection(YfgDeskOptions.SectionName).Bind(opts);

        // Database connection: env var YFGDESK_DB overrides everything.
        var envDb = Environment.GetEnvironmentVariable("YFGDESK_DB");
        if (!string.IsNullOrEmpty(envDb))
            opts.DatabaseConnectionString = envDb;
        else if (string.IsNullOrEmpty(opts.DatabaseConnectionString))
        {
            var env = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT");
            opts.DatabaseConnectionString = env == "Production"
                ? "Data Source=/data/yfgdesk.db"
                : "Data Source=yfgdesk.db";
        }

        // TURN env vars.
        var turnUrls = Environment.GetEnvironmentVariable("YFGDESK_TURN_URLS");
        if (!string.IsNullOrEmpty(turnUrls))
            opts.Turn.Urls = turnUrls.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var turnSecret = Environment.GetEnvironmentVariable("YFGDESK_TURN_SHARED_SECRET");
        if (!string.IsNullOrEmpty(turnSecret))
            opts.Turn.SharedSecret = turnSecret;
        var turnRealm = Environment.GetEnvironmentVariable("YFGDESK_TURN_REALM");
        if (!string.IsNullOrEmpty(turnRealm))
            opts.Turn.Realm = turnRealm;

        return opts;
    }
}
