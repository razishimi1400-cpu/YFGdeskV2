using Microsoft.EntityFrameworkCore;
using YfgDesk.Shared.Protocol;

namespace YfgDesk.Server.Data;

/// <summary>
/// EF Core DbContext for the SQLite-backed YFGDESK server.
/// Uses DateTime (UTC) only — SQLite provider cannot translate DateTimeOffset LINQ.
/// </summary>
public sealed class YfgDeskDbContext : DbContext
{
    public YfgDeskDbContext(DbContextOptions<YfgDeskDbContext> options) : base(options) { }

    public DbSet<DeviceEntity> Devices => Set<DeviceEntity>();
    public DbSet<SessionEntity> Sessions => Set<SessionEntity>();
    public DbSet<AuditEventEntity> AuditEvents => Set<AuditEventEntity>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<DeviceEntity>(e =>
        {
            e.ToTable("devices");
            e.HasKey(x => x.DeviceId);
            e.Property(x => x.DeviceId).HasMaxLength(9);
            e.Property(x => x.PublicKeySpki).IsRequired();
            e.Property(x => x.PublicKeyDigest).IsRequired().HasMaxLength(64);
            e.HasIndex(x => x.PublicKeyDigest).IsUnique();
            e.Property(x => x.BearerDigest).HasMaxLength(64);
            e.HasIndex(x => x.BearerDigest);
            e.Property(x => x.Role).HasMaxLength(16).IsRequired();
            e.Property(x => x.LastSeenIp).HasMaxLength(64);
            e.Property(x => x.LastSeenPorts).HasMaxLength(256);
            e.Property(x => x.PublicIps).HasMaxLength(512);
            e.Property(x => x.DisplayName).HasMaxLength(64);
        });

        b.Entity<SessionEntity>(e =>
        {
            e.ToTable("sessions");
            e.HasKey(x => x.SessionId);
            e.Property(x => x.SessionId).HasMaxLength(40);
            e.Property(x => x.HostDeviceId).HasMaxLength(9).IsRequired();
            e.Property(x => x.ViewerDeviceId).HasMaxLength(9).IsRequired();
            e.Property(x => x.StateRaw).HasConversion<int>();
            e.Property(x => x.VideoQualityMode).HasMaxLength(16).IsRequired();
            e.Property(x => x.ViewerDisplayName).HasMaxLength(64).IsRequired();
            e.Property(x => x.EndReason).HasMaxLength(64);
            e.HasIndex(x => x.HostDeviceId);
            e.HasIndex(x => x.ViewerDeviceId);
            e.HasIndex(x => x.ExpiresAt);
            e.HasIndex(x => x.StateRaw);
        });

        b.Entity<AuditEventEntity>(e =>
        {
            e.ToTable("audit_events");
            e.HasKey(x => x.Id);
            e.Property(x => x.Kind).HasMaxLength(64).IsRequired();
            e.Property(x => x.DeviceId).HasMaxLength(9);
            e.Property(x => x.SessionId).HasMaxLength(40);
            e.Property(x => x.IpHint).HasMaxLength(64);
            e.Property(x => x.Detail).HasMaxLength(256);
            e.HasIndex(x => x.CreatedAt);
            e.HasIndex(x => x.DeviceId);
            e.HasIndex(x => x.SessionId);
        });

        // Shadow-convert enum value: SessionState is stored as int via StateRaw.
        // (No conversion needed.)
        _ = b;
    }

    /// <summary>
    /// Convenience helper to record an audit event. Detail is sanitized/truncated
    /// by <see cref="AuditLog"/> before reaching this layer.
    /// </summary>
    public async Task AuditAsync(string kind, string? deviceId, string? sessionId, string? ipHint, string? detail,
        CancellationToken ct = default)
    {
        AuditEvents.Add(new AuditEventEntity
        {
            CreatedAt = DateTime.UtcNow,
            Kind = kind,
            DeviceId = deviceId,
            SessionId = sessionId,
            IpHint = ipHint,
            Detail = detail,
        });
        try
        {
            await SaveChangesAsync(ct);
        }
        catch (Exception)
        {
            // Audit failures must not bring down the request path. Detach.
            foreach (var entry in ChangeTracker.Entries().ToList())
                entry.State = EntityState.Detached;
        }
    }
}
