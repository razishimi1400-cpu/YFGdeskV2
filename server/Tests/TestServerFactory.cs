using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using YfgDesk.Server.Data;

namespace YfgDesk.Server.Tests;

/// <summary>
/// Test server factory that swaps the DbContext for an isolated SQLite file per test instance.
/// </summary>
public class TestServerFactory : WebApplicationFactory<Program>
{
    private readonly string _dbFile;

    public TestServerFactory()
    {
        _dbFile = Path.Combine(Path.GetTempPath(), $"yfgdesk-test-{Guid.NewGuid():N}.db");
    }

    public string DatabaseConnectionString => $"Data Source={_dbFile}";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureServices(services =>
        {
            // Remove the existing DbContext + options registrations.
            services.RemoveAll<DbContextOptions<YfgDeskDbContext>>();
            services.RemoveAll<YfgDeskDbContext>();

            services.AddDbContext<YfgDeskDbContext>(o =>
            {
                o.UseSqlite(DatabaseConnectionString);
            });

            // Force YfgDeskOptions to point at our test DB.
            services.RemoveAll<YfgDeskOptions>();
            var opts = new YfgDeskOptions
            {
                DatabaseConnectionString = DatabaseConnectionString,
                // Tighter rate limits / TTLs for tests so we exercise them quickly.
                ChallengeTtlSeconds = 5,
                ConsentWaitSeconds = 5,
                SessionIdleSeconds = 2,
                SweepIntervalSeconds = 1,
                WsKeepAliveSeconds = 5,
                HeartbeatIntervalSeconds = 5,
                ReplayWindowSize = 8,
                TimestampSkewSeconds = 5,
                EnrollIpRateLimitPerMinute = 30,
                WsDeviceRateLimitPerMinute = 30,
                BearerTtlSeconds = 3600,
                SessionMaxLifetimeSeconds = 60,
            };
            services.AddSingleton(opts);

            // Build a service provider to run EnsureCreated before requests arrive.
            var sp = services.BuildServiceProvider();
            using var scope = sp.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<YfgDeskDbContext>();
            db.Database.EnsureCreated();
        });
    }

    protected override void Dispose(bool disposing)
    {
        try
        {
            if (File.Exists(_dbFile)) File.Delete(_dbFile);
        }
        catch { /* best-effort */ }
        base.Dispose(disposing);
    }
}
