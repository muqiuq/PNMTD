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

        // Redmine (docs/audit-redmine-zabbix-plan.md §6.5, §6.7).
        public DbSet<RedmineLinkEntity> RedmineLinks { get; set; }
        public DbSet<RedmineIssueEntity> RedmineIssues { get; set; }

        // Zabbix (docs/audit-redmine-zabbix-plan.md §7.3): the hosts' mirror.
        public DbSet<ZabbixHostEntity> ZabbixHosts { get; set; }
        public DbSet<ZabbixSensorEntity> ZabbixSensors { get; set; }

        // Groups and rights (docs/audit-redmine-zabbix-plan.md §5).
        public DbSet<GroupEntity> Groups { get; set; }
        public DbSet<GroupRightEntity> GroupRights { get; set; }
        public DbSet<UserGroupEntity> UserGroups { get; set; }

        public DbSet<AuditLogEntity> AuditLog { get; set; }

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

        // Servers with an agent, Windows and Linux (docs/windows-sensor-plan.md §8.3; docs/linux-sensor-plan.md §3.3, §9.3).
        public DbSet<AgentSettingsEntity> AgentSettings { get; set; }
        public DbSet<AgentJoinTokenEntity> AgentJoinTokens { get; set; }
        public DbSet<AgentEntity> Agents { get; set; }
        public DbSet<AgentLogEntity> AgentLog { get; set; }
        public DbSet<AgentSectionEntity> AgentSections { get; set; }
        public DbSet<AgentLoginEntity> AgentLogins { get; set; }
        public DbSet<AgentChangeEntity> AgentChanges { get; set; }

        // SSH keys (docs/linux-sensor-plan.md §6.3–§6.5): the key manager, where each key is authorized, and the removals.
        public DbSet<SshKeyEntity> SshKeys { get; set; }
        public DbSet<SshKeyAuthorizationEntity> SshKeyAuthorizations { get; set; }
        public DbSet<SshKeyRemovalEntity> SshKeyRemovals { get; set; }

        // Diagnostics (docs/windows-sensor-plan.md §5).
        public DbSet<DiagnosticSetEntity> DiagnosticSets { get; set; }
        public DbSet<DiagnosticCheckEntity> DiagnosticChecks { get; set; }
        public DbSet<DiagnosticAssignmentEntity> DiagnosticAssignments { get; set; }
        public DbSet<DiagnosticRunEntity> DiagnosticRuns { get; set; }

        // Scripts (docs/windows-sensor-plan.md §7, W6; docs/linux-sensor-plan.md §8); the run-as accounts are Windows'.
        public DbSet<AgentScriptEntity> AgentScripts { get; set; }
        public DbSet<AgentScriptVersionEntity> AgentScriptVersions { get; set; }
        public DbSet<WindowsRunAsEntity> WindowsRunAs { get; set; }
        public DbSet<AgentScriptRunEntity> AgentScriptRuns { get; set; }

        // Locations (docs/locations-knowledge-plan.md §3, §4, §8.2).
        public DbSet<LocationEntity> Locations { get; set; }
        public DbSet<LocationFieldEntity> LocationFields { get; set; }
        public DbSet<LocationValueEntity> LocationValues { get; set; }
        public DbSet<LocationHistoryEntity> LocationHistory { get; set; }

        // The knowledge base (docs/locations-knowledge-plan.md §6, §7, §8.2).
        public DbSet<KbPageEntity> KbPages { get; set; }
        public DbSet<KbRevisionEntity> KbRevisions { get; set; }
        public DbSet<KbTagEntity> KbTags { get; set; }
        public DbSet<KbPageTagEntity> KbPageTags { get; set; }
        public DbSet<KbLinkEntity> KbLinks { get; set; }
        public DbSet<KbPageRefEntity> KbPageRefs { get; set; }
        public DbSet<KbPageViewEntity> KbPageViews { get; set; }
        public DbSet<KbAiQuestionEntity> KbAiQuestions { get; set; }

        // Uploaded files (docs/locations-knowledge-plan.md §5.5, §8.2): their metadata; the bytes are in the files folder.
        public DbSet<FileEntity> Files { get; set; }
        public DbSet<FileUseEntity> FileUses { get; set; }

        // Cookie (docs/cookie-plan.md §19.2): its settings' versions, its state, the case book, the duties and the outbox.
        public DbSet<CookieSettingEntity> CookieSettings { get; set; }
        public DbSet<CookieRuntimeEntity> CookieRuntime { get; set; }
        public DbSet<CookieCaseEntity> CookieCases { get; set; }
        public DbSet<CookieCaseEntryEntity> CookieCaseEntries { get; set; }
        public DbSet<CookieDutyEntity> CookieDuties { get; set; }
        public DbSet<CookieOutboxEntity> CookieOutbox { get; set; }
        public DbSet<CookieRoundEntity> CookieRounds { get; set; }
        public DbSet<CookieTraceEntity> CookieTrace { get; set; }
        public DbSet<CookieMessageEntity> CookieMessages { get; set; }
        public DbSet<CookiePseudonymEntity> CookiePseudonyms { get; set; }

        public PnmtdDbContext(DbContextOptions<PnmtdDbContext> options) : base(options)
        {
        }

        /// <summary>
        /// The one place the provider is chosen, for the app, the CLI and the tests. No lazy loading:
        /// every navigation a query needs is in an Include or a projection, because each lazy load
        /// would be a round trip (and PostgreSQL has no MARS for one inside an open reader).
        /// </summary>
        public static DbContextOptionsBuilder Configure(DbContextOptionsBuilder options, string connectionString) =>
            options.UseNpgsql(connectionString).AddInterceptors(Audit.AuditChangesInterceptor.Instance);

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
            // Zabbix's hosts (docs/audit-redmine-zabbix-plan.md §7.3): found by their names.
            modelBuilder.Entity<ZabbixHostEntity>(e =>
            {
                e.HasIndex(h => h.TechnicalName);
                e.HasIndex(h => h.VisibleName);
            });
            // A Zabbix host is attached at most once (§7.7); the sensor's row goes with it.
            modelBuilder.Entity<ZabbixSensorEntity>(e =>
            {
                e.HasOne(z => z.Sensor).WithOne().HasForeignKey<ZabbixSensorEntity>(z => z.SensorId).OnDelete(DeleteBehavior.Cascade);
                e.HasIndex(z => z.ZabbixHostId).IsUnique();
                e.Property(z => z.MinSeverity).HasConversion<string>().HasMaxLength(16);
            });

            // Redmine's links (docs/audit-redmine-zabbix-plan.md §6.5): exactly one target, removed with it; a ticket once per target.
            modelBuilder.Entity<RedmineLinkEntity>(e =>
            {
                e.ToTable(t => t.HasCheckConstraint("CK_redmine_links_one_target", "num_nonnulls(\"PageId\", \"LocationId\", \"HostId\", \"SensorId\") = 1"));
                e.HasOne<KbPageEntity>().WithMany().HasForeignKey(l => l.PageId).OnDelete(DeleteBehavior.Cascade);
                e.HasOne<LocationEntity>().WithMany().HasForeignKey(l => l.LocationId).OnDelete(DeleteBehavior.Cascade);
                e.HasOne<HostEntity>().WithMany().HasForeignKey(l => l.HostId).OnDelete(DeleteBehavior.Cascade);
                e.HasOne<SensorEntity>().WithMany().HasForeignKey(l => l.SensorId).OnDelete(DeleteBehavior.Cascade);
                e.HasIndex(l => new { l.PageId, l.IssueId }).IsUnique().HasFilter("\"PageId\" IS NOT NULL");
                e.HasIndex(l => new { l.LocationId, l.IssueId }).IsUnique().HasFilter("\"LocationId\" IS NOT NULL");
                e.HasIndex(l => new { l.HostId, l.IssueId }).IsUnique().HasFilter("\"HostId\" IS NOT NULL");
                e.HasIndex(l => new { l.SensorId, l.IssueId }).IsUnique().HasFilter("\"SensorId\" IS NOT NULL");
                e.HasIndex(l => l.IssueId);
            });

            // Groups (docs/audit-redmine-zabbix-plan.md §5.1): levels as text, so a renamed enum member can't shift them.
            modelBuilder.Entity<GroupEntity>().HasIndex(g => g.NormalizedName).IsUnique();
            modelBuilder.Entity<GroupRightEntity>(e =>
            {
                e.HasKey(r => new { r.GroupId, r.Area });
                e.Property(r => r.Area).HasConversion<string>().HasMaxLength(32);
                e.Property(r => r.Level).HasConversion<string>().HasMaxLength(16);
                e.HasOne<GroupEntity>().WithMany(g => g.Rights).HasForeignKey(r => r.GroupId).OnDelete(DeleteBehavior.Cascade);
            });
            modelBuilder.Entity<UserGroupEntity>(e =>
            {
                e.HasKey(m => new { m.UserId, m.GroupId });
                e.HasOne<UserEntity>().WithMany(u => u.Groups).HasForeignKey(m => m.UserId).OnDelete(DeleteBehavior.Cascade);
                e.HasOne(m => m.Group).WithMany(g => g.Members).HasForeignKey(m => m.GroupId).OnDelete(DeleteBehavior.Cascade);
                e.HasIndex(m => m.GroupId);
            });
            // The audit log (docs/audit-redmine-zabbix-plan.md §3.2): text for the enums, so that later actors need no migration.
            modelBuilder.Entity<AuditLogEntity>(e =>
            {
                e.Property(l => l.Category).HasConversion<string>().HasMaxLength(32);
                e.Property(l => l.Actor).HasConversion<string>().HasMaxLength(32);
                e.Property(l => l.Outcome).HasConversion<string>().HasMaxLength(16);
                e.HasIndex(l => l.CreatedAt);
                e.HasIndex(l => new { l.UserId, l.CreatedAt });
                e.HasIndex(l => new { l.EventType, l.CreatedAt });
                e.HasIndex(l => l.TargetId);
                e.HasIndex(l => l.Subjects).HasMethod("gin");
            });

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

            // Servers with an agent (docs/windows-sensor-plan.md §8.3): the agent goes with its sensor, its log stays.
            modelBuilder.Entity<AgentJoinTokenEntity>(e =>
            {
                e.HasIndex(t => t.TokenHash).IsUnique();
                e.HasIndex(t => t.CreatedAt);
                e.HasOne<HostEntity>().WithMany().HasForeignKey(t => t.HostId).OnDelete(DeleteBehavior.Cascade);
                e.HasOne<SensorEntity>().WithMany().HasForeignKey(t => t.SensorId).OnDelete(DeleteBehavior.Cascade);
            });
            modelBuilder.Entity<AgentEntity>(e =>
            {
                e.HasOne(a => a.Sensor).WithOne().HasForeignKey<AgentEntity>(a => a.SensorId).OnDelete(DeleteBehavior.Cascade);
                e.HasIndex(a => a.AgentId).IsUnique();
                e.HasIndex(a => a.MachineId);
            });
            modelBuilder.Entity<AgentSectionEntity>(e =>
            {
                e.HasKey(x => new { x.SensorId, x.Section });
                e.HasOne<SensorEntity>().WithMany().HasForeignKey(x => x.SensorId).OnDelete(DeleteBehavior.Cascade);
            });
            modelBuilder.Entity<AgentLoginEntity>(e =>
            {
                e.HasOne<SensorEntity>().WithMany().HasForeignKey(l => l.SensorId).OnDelete(DeleteBehavior.Cascade);
                e.HasIndex(l => new { l.SensorId, l.Channel, l.RecordId }).IsUnique();
                e.HasIndex(l => new { l.SensorId, l.At });
                e.HasIndex(l => l.At);
                // Linux (docs/linux-sensor-plan.md §5.6, §6.3): the logins with a key, for the filter and the keys' last use.
                e.HasIndex(l => l.KeyFingerprint);
            });
            modelBuilder.Entity<AgentChangeEntity>(e =>
            {
                e.HasOne<SensorEntity>().WithMany().HasForeignKey(c => c.SensorId).OnDelete(DeleteBehavior.Cascade);
                e.HasIndex(c => new { c.SensorId, c.At });
                e.HasIndex(c => c.At);
            });
            // SSH keys (docs/linux-sensor-plan.md §9.3): joined by the fingerprint's value. A key's owner may be a user (set null
            // when the user goes); a server's authorizations go with it.
            modelBuilder.Entity<SshKeyEntity>(e =>
            {
                e.HasIndex(k => k.Fingerprint).IsUnique();
                e.HasIndex(k => k.UserId);
                e.HasOne<UserEntity>().WithMany().HasForeignKey(k => k.UserId).OnDelete(DeleteBehavior.SetNull);
            });
            modelBuilder.Entity<SshKeyAuthorizationEntity>(e =>
            {
                e.HasOne<SensorEntity>().WithMany().HasForeignKey(a => a.SensorId).OnDelete(DeleteBehavior.Cascade);
                e.HasIndex(a => new { a.SensorId, a.Account, a.Path, a.Fingerprint }).IsUnique();
                e.HasIndex(a => a.Fingerprint);
            });
            // The removals outlive their server (docs/linux-sensor-plan.md §6.5).
            modelBuilder.Entity<SshKeyRemovalEntity>(e =>
            {
                e.HasOne<SensorEntity>().WithMany().HasForeignKey(r => r.SensorId).OnDelete(DeleteBehavior.SetNull);
                e.HasIndex(r => new { r.SensorId, r.State });
                e.HasIndex(r => r.Fingerprint);
                e.HasIndex(r => r.RequestedAt);
                e.HasIndex(r => r.BatchId);
            });
            modelBuilder.Entity<DiagnosticSetEntity>(e =>
            {
                e.HasIndex(x => x.Name).IsUnique();
                e.HasMany(x => x.Checks).WithOne().HasForeignKey(c => c.SetId).OnDelete(DeleteBehavior.Cascade);
            });
            modelBuilder.Entity<DiagnosticAssignmentEntity>(e =>
            {
                e.HasOne<DiagnosticSetEntity>().WithMany().HasForeignKey(a => a.SetId).OnDelete(DeleteBehavior.Restrict);
                e.HasOne<SensorEntity>().WithMany().HasForeignKey(a => a.TargetSensorId).OnDelete(DeleteBehavior.Cascade);
                e.HasOne<SensorEntity>().WithMany().HasForeignKey(a => a.SensorId).OnDelete(DeleteBehavior.Cascade);
                e.HasIndex(a => a.TargetSensorId);
                e.HasIndex(a => a.SensorId).IsUnique();
            });
            modelBuilder.Entity<DiagnosticRunEntity>(e =>
            {
                e.HasOne<DiagnosticAssignmentEntity>().WithMany().HasForeignKey(r => r.AssignmentId).OnDelete(DeleteBehavior.Cascade);
                e.HasIndex(r => new { r.AssignmentId, r.StartedAt });
                e.HasIndex(r => r.StartedAt);
            });
            modelBuilder.Entity<AgentScriptEntity>(e =>
            {
                e.HasIndex(x => x.Name).IsUnique();
                e.HasMany(x => x.Versions).WithOne().HasForeignKey(v => v.ScriptId).OnDelete(DeleteBehavior.Cascade);
            });
            modelBuilder.Entity<AgentScriptVersionEntity>(e =>
            {
                e.HasIndex(v => new { v.ScriptId, v.Number }).IsUnique();
                e.HasIndex(v => new { v.ParseState, v.Hash });
            });
            modelBuilder.Entity<WindowsRunAsEntity>().HasIndex(x => x.Name).IsUnique();
            modelBuilder.Entity<AgentScriptRunEntity>(e =>
            {
                // The history outlives its script and its server.
                e.HasOne<AgentScriptEntity>().WithMany().HasForeignKey(r => r.ScriptId).OnDelete(DeleteBehavior.SetNull);
                e.HasOne<SensorEntity>().WithMany().HasForeignKey(r => r.SensorId).OnDelete(DeleteBehavior.SetNull);
                e.HasIndex(r => r.StartedAt);
                e.HasIndex(r => new { r.SensorId, r.StartedAt });
                e.HasIndex(r => r.BatchId);
            });
            modelBuilder.Entity<AgentLogEntity>(e =>
            {
                e.HasOne<SensorEntity>().WithMany().HasForeignKey(l => l.SensorId).OnDelete(DeleteBehavior.SetNull);
                e.HasIndex(l => new { l.SensorId, l.At });
                e.HasIndex(l => l.At);
            });

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

            ConfigureLocations(modelBuilder);
            ConfigureKnowledgeBase(modelBuilder);
            ConfigureFiles(modelBuilder);
            ConfigureCookie(modelBuilder);

            base.OnModelCreating(modelBuilder);
        }

        /// <summary>
        /// Locations (docs/locations-knowledge-plan.md §8.2): a parent can't be deleted while it has sub-locations (§3.3);
        /// deleting a location unassigns its hosts and takes its values and history along.
        /// </summary>
        private static void ConfigureLocations(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<LocationEntity>(e =>
            {
                e.HasOne(l => l.Parent).WithMany().HasForeignKey(l => l.ParentId).OnDelete(DeleteBehavior.Restrict);
                // Names are unique among siblings, at the top level too, where ParentId is null (§3.1).
                e.HasIndex(l => new { l.ParentId, l.NormalizedName }).IsUnique().AreNullsDistinct(false);
            });
            modelBuilder.Entity<HostEntity>(e =>
            {
                e.HasOne(h => h.AssignedLocation).WithMany().HasForeignKey(h => h.LocationId).OnDelete(DeleteBehavior.SetNull);
                e.HasIndex(h => h.LocationId);
            });
            modelBuilder.Entity<LocationFieldEntity>(e =>
            {
                e.HasIndex(f => f.Key).IsUnique();
                // A new installation can write about its locations at once (§4.4).
                e.HasData(new LocationFieldEntity
                {
                    Id = LocationFieldEntity.OverviewId,
                    Key = LocationFieldEntity.OverviewKey,
                    Label = "Overview",
                    Type = LocationFieldType.Markdown,
                    Position = 0,
                    Overview = true,
                    Inherited = false,
                    Sensitive = false,
                    CreatedAt = new DateTime(2026, 11, 14, 0, 0, 0, DateTimeKind.Utc),
                });
            });
            modelBuilder.Entity<LocationValueEntity>(e =>
            {
                e.HasKey(v => new { v.LocationId, v.FieldId });
                e.HasOne<LocationEntity>().WithMany().HasForeignKey(v => v.LocationId).OnDelete(DeleteBehavior.Cascade);
                // The field's filled count (§10.5) and deleting a field's values (§4.3).
                e.HasOne(v => v.Field).WithMany().HasForeignKey(v => v.FieldId).OnDelete(DeleteBehavior.Cascade);
                e.HasIndex(v => v.FieldId);
            });
            modelBuilder.Entity<LocationHistoryEntity>(e =>
            {
                e.HasOne<LocationEntity>().WithMany().HasForeignKey(h => h.LocationId).OnDelete(DeleteBehavior.Cascade);
                // Deleting a field deletes its values and their history rows (§4.3).
                e.HasOne<LocationFieldEntity>().WithMany().HasForeignKey(h => h.FieldId).OnDelete(DeleteBehavior.Cascade);
                e.HasIndex(h => new { h.LocationId, h.At });
                e.HasIndex(h => h.FieldId);
            });
        }

        /// <summary>
        /// The knowledge base (docs/locations-knowledge-plan.md §8.2): everything of a page goes with it (§6.2), and a link
        /// goes with its target. The indexes serve the lists (§6.6) and the search (§6.7).
        /// </summary>
        private static void ConfigureKnowledgeBase(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<KbPageEntity>(e =>
            {
                // KB-42: a sequence, not the key, never reused (§6.1).
                e.Property(p => p.Number).UseIdentityByDefaultColumn();
                e.HasIndex(p => p.Number).IsUnique();
                // A title and tags before the summary before the content (§6.7). `simple`: no stemming, German and English
                // alike. The plain text is capped below tsvector's 1 MB, so a large page can't fail its save.
                e.Property(p => p.SearchVector).HasComputedColumnSql(
                    """
                    setweight(to_tsvector('simple'::regconfig, coalesce("Title", '') || ' ' || coalesce("TagsText", '')), 'A') ||
                    setweight(to_tsvector('simple'::regconfig, coalesce("Summary", '')), 'B') ||
                    setweight(to_tsvector('simple'::regconfig, left(coalesce("PlainText", ''), 200000)), 'C')
                    """, stored: true);
                e.HasIndex(p => p.SearchVector).HasMethod("GIN");
                // Newest, recently updated, most viewed of all time (§6.6).
                e.HasIndex(p => p.CreatedAt);
                e.HasIndex(p => p.UpdatedAt);
                e.HasIndex(p => p.Views);
                e.HasMany(p => p.Tags).WithOne().HasForeignKey(t => t.PageId).OnDelete(DeleteBehavior.Cascade);
                e.HasMany(p => p.Links).WithOne().HasForeignKey(l => l.PageId).OnDelete(DeleteBehavior.Cascade);
            });
            modelBuilder.Entity<KbRevisionEntity>(e =>
            {
                e.HasOne<KbPageEntity>().WithMany().HasForeignKey(r => r.PageId).OnDelete(DeleteBehavior.Cascade);
                e.HasIndex(r => new { r.PageId, r.Number }).IsUnique();
                // Most edited in the last 30 days (§6.6).
                e.HasIndex(r => r.At);
            });
            modelBuilder.Entity<KbTagEntity>().HasIndex(t => t.NormalizedName).IsUnique();
            modelBuilder.Entity<KbPageTagEntity>(e =>
            {
                e.HasKey(t => new { t.PageId, t.TagId });
                // Pages by tag, and the counts of the tag browser (§6.6).
                e.HasOne(t => t.Tag).WithMany().HasForeignKey(t => t.TagId).OnDelete(DeleteBehavior.Cascade);
                e.HasIndex(t => t.TagId);
            });
            modelBuilder.Entity<KbLinkEntity>(e =>
            {
                e.ToTable(t => t.HasCheckConstraint("CK_kb_links_one_target",
                    "num_nonnulls(\"LocationId\", \"HostId\", \"SensorId\") = 1"));
                e.HasOne<LocationEntity>().WithMany().HasForeignKey(l => l.LocationId).OnDelete(DeleteBehavior.Cascade);
                e.HasOne<HostEntity>().WithMany().HasForeignKey(l => l.HostId).OnDelete(DeleteBehavior.Cascade);
                e.HasOne<SensorEntity>().WithMany().HasForeignKey(l => l.SensorId).OnDelete(DeleteBehavior.Cascade);
                // Once per page and target. The target leads, so the same indexes find a target's pages (GET /kb/related)
                // and serve the cascades; the page's links have an index of their own.
                e.HasIndex(l => new { l.LocationId, l.PageId }).IsUnique().HasFilter("\"LocationId\" IS NOT NULL");
                e.HasIndex(l => new { l.HostId, l.PageId }).IsUnique().HasFilter("\"HostId\" IS NOT NULL");
                e.HasIndex(l => new { l.SensorId, l.PageId }).IsUnique().HasFilter("\"SensorId\" IS NOT NULL");
                e.HasIndex(l => l.PageId);
            });
            modelBuilder.Entity<KbPageRefEntity>(e =>
            {
                e.HasKey(r => new { r.FromPageId, r.ToNumber });
                e.HasOne<KbPageEntity>().WithMany().HasForeignKey(r => r.FromPageId).OnDelete(DeleteBehavior.Cascade);
                // Linked from (§6.1).
                e.HasIndex(r => r.ToNumber);
            });
            modelBuilder.Entity<KbPageViewEntity>(e =>
            {
                e.HasKey(v => new { v.PageId, v.Day });
                e.HasOne<KbPageEntity>().WithMany().HasForeignKey(v => v.PageId).OnDelete(DeleteBehavior.Cascade);
                // Most viewed in the last 30 days (§6.6).
                e.HasIndex(v => v.Day);
            });
            modelBuilder.Entity<KbAiQuestionEntity>(e =>
            {
                // One row per page and person, its time moved on each question (§7.2).
                e.HasKey(q => new { q.PageId, q.UserId });
                e.HasOne<KbPageEntity>().WithMany().HasForeignKey(q => q.PageId).OnDelete(DeleteBehavior.Cascade);
                e.HasIndex(q => q.AskedAt);
            });
        }

        /// <summary>
        /// Uploaded files (docs/locations-knowledge-plan.md §5.5, §8.2): a use goes with its file, its page or its location.
        /// </summary>
        private static void ConfigureFiles(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<FileEntity>(e =>
            {
                // Stored once per content (§5.5): a file marked deleted no longer counts, so the same bytes can come back.
                e.HasIndex(f => f.Sha256).IsUnique().HasFilter("\"DeletedAt\" IS NULL");
                // The cleanup: unused uploads after 24 hours, deleted files after 7 days (§5.5).
                e.HasIndex(f => f.CreatedAt);
                e.HasIndex(f => f.DeletedAt);
            });
            modelBuilder.Entity<FileUseEntity>(e =>
            {
                e.ToTable(t => t.HasCheckConstraint("CK_file_uses_one_owner", "num_nonnulls(\"PageId\", \"LocationId\") = 1"));
                e.HasOne<FileEntity>().WithMany().HasForeignKey(u => u.FileId).OnDelete(DeleteBehavior.Cascade);
                e.HasOne<KbPageEntity>().WithMany().HasForeignKey(u => u.PageId).OnDelete(DeleteBehavior.Cascade);
                e.HasOne<LocationEntity>().WithMany().HasForeignKey(u => u.LocationId).OnDelete(DeleteBehavior.Cascade);
                // Once per file and owner. The owner leads: a page's attachments (§6.1) and the cascades; where a file is
                // used, and whether at all, by FileId.
                e.HasIndex(u => new { u.PageId, u.FileId }).IsUnique().HasFilter("\"PageId\" IS NOT NULL");
                e.HasIndex(u => new { u.LocationId, u.FileId }).IsUnique().HasFilter("\"LocationId\" IS NOT NULL");
                e.HasIndex(u => u.FileId);
            });
        }

        /// <summary>Cookie (docs/cookie-plan.md §19.2): enums as text, one open case per key, a duty once per case and key.</summary>
        private static void ConfigureCookie(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<CookieSettingEntity>().HasIndex(s => new { s.Section, s.Version }).IsUnique();
            modelBuilder.Entity<CookieRuntimeEntity>(e =>
            {
                e.Property(r => r.State).HasConversion<string>().HasMaxLength(16);
                e.Property(r => r.CodeStatus).HasConversion<string>().HasMaxLength(16);
                e.Property(r => r.CookieStatus).HasConversion<string>().HasMaxLength(16);
            });
            modelBuilder.Entity<CookieCaseEntity>(e =>
            {
                e.Property(c => c.Kind).HasConversion<string>().HasMaxLength(16);
                e.Property(c => c.Importance).HasConversion<string>().HasMaxLength(16);
                e.HasIndex(c => c.Key).IsUnique().HasFilter("\"Open\"");
                e.HasIndex(c => new { c.Open, c.Kind });
                e.HasIndex(c => c.IssueId);
                e.HasIndex(c => c.HostId);
                e.HasIndex(c => c.ClosedAt);
            });
            modelBuilder.Entity<CookieCaseEntryEntity>(e =>
            {
                e.HasOne<CookieCaseEntity>().WithMany().HasForeignKey(x => x.CaseId).OnDelete(DeleteBehavior.Cascade);
                e.HasIndex(x => new { x.CaseId, x.At });
            });
            modelBuilder.Entity<CookieDutyEntity>(e =>
            {
                e.Property(d => d.Kind).HasConversion<string>().HasMaxLength(32);
                e.Property(d => d.State).HasConversion<string>().HasMaxLength(16);
                e.HasOne<CookieCaseEntity>().WithMany().HasForeignKey(d => d.CaseId).OnDelete(DeleteBehavior.Cascade);
                e.HasIndex(d => new { d.CaseId, d.Key }).IsUnique();
                e.HasIndex(d => new { d.State, d.DueAt });
            });
            modelBuilder.Entity<CookieOutboxEntity>(e =>
            {
                e.Property(o => o.Channel).HasConversion<string>().HasMaxLength(32);
                e.Property(o => o.State).HasConversion<string>().HasMaxLength(16);
                e.HasOne<CookieCaseEntity>().WithMany().HasForeignKey(o => o.CaseId).OnDelete(DeleteBehavior.SetNull);
                e.HasIndex(o => o.CreatedAt);
                e.HasIndex(o => new { o.Channel, o.UserId, o.CreatedAt });
                e.HasIndex(o => new { o.IssueId, o.CreatedAt });
            });
            modelBuilder.Entity<CookieRoundEntity>().HasIndex(r => r.StartedAt);
            modelBuilder.Entity<CookieTraceEntity>(e =>
            {
                e.HasIndex(t => t.RoundId);
                e.HasIndex(t => new { t.CaseId, t.Id });
                e.HasIndex(t => t.At);
            });
            modelBuilder.Entity<CookieMessageEntity>(e =>
            {
                e.HasOne<UserEntity>().WithMany().HasForeignKey(m => m.UserId).OnDelete(DeleteBehavior.Cascade);
                e.HasIndex(m => new { m.UserId, m.Id });
                e.HasIndex(m => m.Answered).HasFilter("NOT \"Answered\"");
            });
            modelBuilder.Entity<CookiePseudonymEntity>(e =>
            {
                e.HasIndex(p => new { p.Kind, p.Value }).IsUnique();
                e.HasIndex(p => p.Placeholder).IsUnique();
            });
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
