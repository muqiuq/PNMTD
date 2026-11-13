using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Npgsql;
using PNMTD.Lib.Helper;
using PNMTD.Models.Db;

namespace PNMTD.Data
{
    /// <summary>
    /// PNMTD's database: PostgreSQL (docs/webui-migration-plan.md §3.4). Configured from the
    /// Database:* settings; see <see cref="DatabaseSettings"/>.
    /// </summary>
    public class PnmtdDbContext : DbContext
    {
        public DbSet<HostEntity> Hosts { get; set; }

        public DbSet<SensorEntity> Sensors { get; set; }

        public DbSet<EventEntity> Events { get; set; }

        public DbSet<NotificationRuleEntity> NotificationRules { get; set; }

        public DbSet<NotificationRuleEventEntity> NotificationRuleEvents { get; set; }

        public DbSet<NotificationRuleSensorEntity> NotificationRuleSensor { get; set; }

        // Scopes and exclusions (docs/ai-operations-plan.md §11.2).
        public DbSet<NotificationRuleScopeEntity> NotificationRuleScopes { get; set; }
        public DbSet<NotificationRuleExclusionEntity> NotificationRuleExclusions { get; set; }

        public DbSet<MailInputRuleEntity> MailInputs { get; set; }

        public DbSet<KeyValueEntity> KeyValues { get; set; }

        public DbSet<MailLogEntity> MailLogs { get; set; }

        public DbSet<DnsZoneEntity> DnsZones { get; set; }
        public DbSet<DnsZoneEntryEntity> DnsZoneEntries { get; set; }
        public DbSet<DnsZoneLogEntryEntity> DnsZoneLogEntries { get; set; }

        public DbSet<UserEntity> Users { get; set; }

        public DbSet<AuthLogEntity> AuthLog { get; set; }

        // UDP nodes (docs/udp-iot-plan.md §4.3).
        public DbSet<UdpNodeEntity> UdpNodes { get; set; }
        public DbSet<UdpLastPacketEntity> UdpLastPackets { get; set; }
        public DbSet<UdpCommandEntity> UdpCommands { get; set; }
        public DbSet<UdpCommandLogEntity> UdpCommandLog { get; set; }
        public DbSet<UdpFirmwareEntity> UdpFirmware { get; set; }
        public DbSet<UdpOtaJobEntity> UdpOtaJobs { get; set; }

        // MikroTik routers (docs/mikrotik-plan.md §9.2).
        public DbSet<MikrotikRouterEntity> MikrotikRouters { get; set; }
        public DbSet<MikrotikSettingsEntity> MikrotikSettings { get; set; }
        public DbSet<MikrotikSshKeyEntity> MikrotikSshKeys { get; set; }

        public DbSet<MikrotikBackupKeyEntity> MikrotikBackupKeys { get; set; }
        public DbSet<MikrotikBackupEntity> MikrotikBackups { get; set; }
        public DbSet<MikrotikJobEntity> MikrotikJobs { get; set; }
        public DbSet<MikrotikJobLogEntity> MikrotikJobLog { get; set; }

        // The WireGuard network (docs/mikrotik-plan.md §3, §9.2).
        public DbSet<WgNetworkEntity> WgNetwork { get; set; }
        public DbSet<WgPeerEntity> WgPeers { get; set; }
        public DbSet<WgPeerStatusEntity> WgPeerStatus { get; set; }
        public DbSet<WgPeerAccessEntity> WgPeerAccess { get; set; }

        // The uplink's history (docs/ai-operations-plan.md §7.2).
        public DbSet<UplinkCheckEntity> UplinkChecks { get; set; }

        // The AI (docs/ai-operations-plan.md §3.5–§3.7).
        public DbSet<AiWorkEntity> AiWork { get; set; }
        public DbSet<AiRequestEntity> AiRequests { get; set; }
        public DbSet<AiSpendingEntity> AiSpending { get; set; }
        public DbSet<AiBudgetStateEntity> AiBudgetState { get; set; }

        // Decisions (docs/ai-operations-plan.md §5).
        public DbSet<DecisionEntity> Decisions { get; set; }

        // RouterOS releases and their assessment (docs/ai-operations-plan.md §10).
        public DbSet<MikrotikReleaseEntity> MikrotikReleases { get; set; }

        public PnmtdDbContext(DbContextOptions<PnmtdDbContext> options) : base(options)
        {
        }

        /// <summary>
        /// The one place the provider is chosen, for the app, the CLI and the tests. No lazy loading:
        /// every navigation a query needs is in an Include or a projection, because each lazy load
        /// would be a round trip (and PostgreSQL has no MARS for one inside an open reader).
        /// </summary>
        public static DbContextOptionsBuilder Configure(DbContextOptionsBuilder options, string connectionString) =>
            options.UseNpgsql(connectionString);

        public static PnmtdDbContext Create(string connectionString) =>
            new(((DbContextOptionsBuilder<PnmtdDbContext>)Configure(new DbContextOptionsBuilder<PnmtdDbContext>(), connectionString)).Options);

        protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
        {
            // timestamptz takes UTC only. Code from before 1.0 may still hand over an Unspecified
            // value (an unset property, a value parsed somewhere): taken as UTC, as Clock.AsUtc does.
            configurationBuilder.Properties<DateTime>().HaveConversion<UtcDateTimeConverter>();
            configurationBuilder.Properties<DateTime?>().HaveConversion<NullableUtcDateTimeConverter>();
        }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<KeyValueEntity>()
                .HasIndex(k => new { k.Key })
                .IsUnique(true);

            // Lower-case like every other table (it had no [Table] attribute).
            modelBuilder.Entity<NotificationRuleSensorEntity>().ToTable("notificationrulesensor");

            // The sensor API looks sensors up by token on every request. Not unique: v0.12 databases
            // can hold duplicates, which keep failing as they always did (§3.3).
            modelBuilder.Entity<SensorEntity>().HasIndex(s => s.SecretWriteToken);
            modelBuilder.Entity<SensorEntity>().HasIndex(s => s.SecretReadToken);

            // Latest event per sensor, events of a sensor in a time range, and retention.
            modelBuilder.Entity<EventEntity>().HasIndex(e => new { e.SensorId, e.Created });
            modelBuilder.Entity<EventEntity>().HasIndex(e => e.Created);

            modelBuilder.Entity<UserEntity>().HasIndex(u => u.NormalizedUsername).IsUnique();
            modelBuilder.Entity<AuthLogEntity>().HasIndex(l => l.CreatedAt);
            modelBuilder.Entity<AuthLogEntity>().HasIndex(l => new { l.UserId, l.CreatedAt });

            // UDP nodes. The node and its last packets go with the sensor; commands, their log and
            // updates are permanent and keep a snapshot of the node instead (§4.3).
            modelBuilder.Entity<UdpNodeEntity>(e =>
            {
                e.HasOne(n => n.Sensor).WithOne().HasForeignKey<UdpNodeEntity>(n => n.SensorId).OnDelete(DeleteBehavior.Cascade);
                e.HasIndex(n => n.DeviceId).IsUnique();
            });
            modelBuilder.Entity<UdpLastPacketEntity>(e =>
            {
                e.HasKey(p => new { p.SensorId, p.Type });
                e.HasOne<SensorEntity>().WithMany().HasForeignKey(p => p.SensorId).OnDelete(DeleteBehavior.Cascade);
            });
            modelBuilder.Entity<UdpCommandEntity>(e =>
            {
                e.HasOne<SensorEntity>().WithMany().HasForeignKey(c => c.SensorId).OnDelete(DeleteBehavior.SetNull);
                e.HasIndex(c => new { c.SensorId, c.State, c.Created });
            });
            modelBuilder.Entity<UdpCommandLogEntity>(e =>
            {
                e.HasOne<SensorEntity>().WithMany().HasForeignKey(l => l.SensorId).OnDelete(DeleteBehavior.SetNull);
                e.HasOne<UdpCommandEntity>().WithMany().HasForeignKey(l => l.CommandId).OnDelete(DeleteBehavior.Restrict);
                e.HasOne<UdpOtaJobEntity>().WithMany().HasForeignKey(l => l.OtaJobId).OnDelete(DeleteBehavior.Restrict);
                e.HasIndex(l => new { l.CommandId, l.Created });
                e.HasIndex(l => new { l.OtaJobId, l.Created });
                e.HasIndex(l => new { l.SensorId, l.Created });
                e.HasIndex(l => l.Created);
            });
            modelBuilder.Entity<UdpOtaJobEntity>(e =>
            {
                e.HasOne<SensorEntity>().WithMany().HasForeignKey(j => j.SensorId).OnDelete(DeleteBehavior.SetNull);
                e.HasOne<UdpFirmwareEntity>().WithMany().HasForeignKey(j => j.FirmwareId).OnDelete(DeleteBehavior.SetNull);
                e.HasIndex(j => new { j.SensorId, j.State, j.Created });
                e.HasIndex(j => j.Created);
            });

            // MikroTik routers: the router goes with its sensor, its backups stay (§9.2).
            modelBuilder.Entity<MikrotikRouterEntity>(e =>
            {
                e.HasOne(r => r.Sensor).WithOne().HasForeignKey<MikrotikRouterEntity>(r => r.SensorId).OnDelete(DeleteBehavior.Cascade);
            });
            modelBuilder.Entity<WgPeerEntity>(e =>
            {
                e.HasOne<SensorEntity>().WithMany().HasForeignKey(p => p.SensorId).OnDelete(DeleteBehavior.Cascade);
                e.HasOne<UserEntity>().WithMany().HasForeignKey(p => p.OwnerUserId).OnDelete(DeleteBehavior.SetNull);
                e.HasIndex(p => p.Address).IsUnique();
                e.HasIndex(p => p.PublicKey).IsUnique();
                e.HasIndex(p => p.SensorId);
            });
            modelBuilder.Entity<WgPeerAccessEntity>(e =>
            {
                e.HasKey(a => new { a.PeerId, a.RouterSensorId });
                e.HasOne<WgPeerEntity>().WithMany().HasForeignKey(a => a.PeerId).OnDelete(DeleteBehavior.Cascade);
                e.HasOne<SensorEntity>().WithMany().HasForeignKey(a => a.RouterSensorId).OnDelete(DeleteBehavior.Cascade);
            });
            modelBuilder.Entity<WgPeerStatusEntity>(e =>
            {
                e.HasOne<WgPeerEntity>().WithOne().HasForeignKey<WgPeerStatusEntity>(s => s.PeerId).OnDelete(DeleteBehavior.Cascade);
            });
            modelBuilder.Entity<MikrotikRouterEntity>()
                .HasOne<WgPeerEntity>().WithMany().HasForeignKey(r => r.WgPeerId).OnDelete(DeleteBehavior.SetNull);
            modelBuilder.Entity<MikrotikJobEntity>(e =>
            {
                e.HasOne<SensorEntity>().WithMany().HasForeignKey(j => j.SensorId).OnDelete(DeleteBehavior.SetNull);
                e.HasIndex(j => new { j.SensorId, j.CreatedAt });
                e.HasIndex(j => new { j.State, j.CreatedAt });
                e.HasIndex(j => j.BatchId);
            });
            modelBuilder.Entity<MikrotikJobLogEntity>(e =>
            {
                e.HasOne<MikrotikJobEntity>().WithMany().HasForeignKey(l => l.JobId).OnDelete(DeleteBehavior.Cascade);
                e.HasIndex(l => new { l.JobId, l.At });
            });
            modelBuilder.Entity<MikrotikBackupEntity>(e =>
            {
                e.HasOne<SensorEntity>().WithMany().HasForeignKey(b => b.SensorId).OnDelete(DeleteBehavior.SetNull);
                e.HasIndex(b => new { b.SensorId, b.CreatedAt });
                e.HasIndex(b => new { b.SensorId, b.Number });
                e.HasIndex(b => b.CreatedAt);
            });

            // One system sensor per key (docs/ai-operations-plan.md §4).
            modelBuilder.Entity<SensorEntity>().HasIndex(s => new { s.Type, s.Parameters }).IsUnique().HasFilter("\"Type\" = 90");
            modelBuilder.Entity<UplinkCheckEntity>().HasIndex(c => c.At);

            modelBuilder.Entity<AiWorkEntity>(e =>
            {
                e.HasIndex(w => new { w.State, w.NextAttemptAt });
                e.HasIndex(w => new { w.SubjectType, w.SubjectId });
            });
            modelBuilder.Entity<AiRequestEntity>(e =>
            {
                e.HasIndex(r => r.At);
                e.HasIndex(r => r.WorkId);
                e.HasOne<AiWorkEntity>().WithMany().HasForeignKey(r => r.WorkId).OnDelete(DeleteBehavior.SetNull);
            });
            modelBuilder.Entity<AiSpendingEntity>().HasKey(s => new { s.Day, s.Purpose, s.Model });

            modelBuilder.Entity<DecisionEntity>(e =>
            {
                e.HasIndex(d => new { d.State, d.Kind });
                // One open decision per key (§5.1).
                e.HasIndex(d => d.DedupeKey).IsUnique().HasFilter("\"State\" = 0");
            });
            modelBuilder.Entity<MikrotikReleaseEntity>().HasIndex(r => new { r.Version, r.Channel }).IsUnique();

            modelBuilder.Entity<NotificationRuleScopeEntity>(e =>
            {
                e.HasOne(s => s.Rule).WithMany(r => r.Scopes).HasForeignKey(s => s.RuleId).OnDelete(DeleteBehavior.Cascade);
                e.HasOne(s => s.Host).WithMany().HasForeignKey(s => s.HostId).OnDelete(DeleteBehavior.Cascade);
            });
            modelBuilder.Entity<NotificationRuleExclusionEntity>(e =>
            {
                e.HasKey(x => new { x.RuleId, x.SensorId });
                e.HasOne<NotificationRuleEntity>().WithMany(r => r.Exclusions).HasForeignKey(x => x.RuleId).OnDelete(DeleteBehavior.Cascade);
                e.HasOne<SensorEntity>().WithMany().HasForeignKey(x => x.SensorId).OnDelete(DeleteBehavior.Cascade);
            });

            base.OnModelCreating(modelBuilder);
        }

        private sealed class UtcDateTimeConverter() : ValueConverter<DateTime, DateTime>(
            v => Clock.AsUtc(v),
            v => DateTime.SpecifyKind(v, DateTimeKind.Utc));

        private sealed class NullableUtcDateTimeConverter() : ValueConverter<DateTime?, DateTime?>(
            v => v.HasValue ? Clock.AsUtc(v.Value) : v,
            v => v.HasValue ? DateTime.SpecifyKind(v.Value, DateTimeKind.Utc) : v);
    }

    /// <summary>
    /// The connection: Database:ConnectionString when set (tests), otherwise built from
    /// Database:Host, :Port, :Name, :Username and :Password (the compose file sets these).
    /// </summary>
    public static class DatabaseSettings
    {
        public static string ConnectionString(IConfiguration configuration)
        {
            var explicitString = configuration["Database:ConnectionString"];
            if (!string.IsNullOrWhiteSpace(explicitString))
            {
                return explicitString;
            }
            var builder = new NpgsqlConnectionStringBuilder
            {
                Host = configuration["Database:Host"] ?? "localhost",
                Port = int.TryParse(configuration["Database:Port"], out var port) ? port : 5432,
                Database = configuration["Database:Name"] ?? "pnmtd",
                Username = configuration["Database:Username"] ?? "pnmtd",
                Password = configuration["Database:Password"],
            };
            return builder.ConnectionString;
        }
    }

    /// <summary>For `dotnet ef migrations add`: never connects.</summary>
    public sealed class PnmtdDesignTimeDbContextFactory : IDesignTimeDbContextFactory<PnmtdDbContext>
    {
        public PnmtdDbContext CreateDbContext(string[] args) =>
            PnmtdDbContext.Create("Host=localhost;Database=pnmtd;Username=pnmtd;Password=design-time");
    }

    public static class DbContextServiceProviderExtensions
    {
        /// <summary>A context of its own, for one run of a background task.</summary>
        public static PnmtdDbContext CreateDbContext(this IServiceProvider services) =>
            services.GetRequiredService<IDbContextFactory<PnmtdDbContext>>().CreateDbContext();
    }
}
