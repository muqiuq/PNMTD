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
