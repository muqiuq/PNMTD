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
        // The boards' history of Zabbix's problems (docs/boards-plan.md §7.5).
        public DbSet<ZabbixProblemHistoryEntity> ZabbixProblemHistory { get; set; }

        // SSL certificate sensors (docs/ssl-certificate-plan.md §4): their settings and state, the certificates seen, the private CAs.
        public DbSet<CertificateSensorEntity> CertificateSensors { get; set; }
        public DbSet<CertificateVersionEntity> CertificateVersions { get; set; }
        public DbSet<CertificateAuthorityEntity> CertificateAuthorities { get; set; }
        public DbSet<CertificateSensorAuthorityEntity> CertificateSensorAuthorities { get; set; }

        // Advanced sensors (docs/advanced-sensor-plan.md §5): the setting and state, the rules, the fields, the reports, the samples.
        public DbSet<AdvancedSensorEntity> AdvancedSensors { get; set; }
        public DbSet<AdvancedRuleEntity> AdvancedRules { get; set; }
        public DbSet<AdvancedFieldEntity> AdvancedFields { get; set; }
        public DbSet<AdvancedReportEntity> AdvancedReports { get; set; }
        public DbSet<AdvancedSampleEntity> AdvancedSamples { get; set; }

        // The network map (docs/network-map-plan.md §9.2): the settings, scans, the routers' latest tables, interfaces, connections, suggestions.
        public DbSet<NetworkMapSettingsEntity> NetworkMapSettings { get; set; }
        public DbSet<NetworkScanEntity> NetworkScans { get; set; }
        public DbSet<NetworkScanItemEntity> NetworkScanItems { get; set; }
        public DbSet<NetworkRouterSnapshotEntity> NetworkRouterSnapshots { get; set; }
        public DbSet<NetworkRouterPortEntity> NetworkRouterPorts { get; set; }
        public DbSet<NetworkObservationEntity> NetworkObservations { get; set; }
        public DbSet<NetworkInterfaceEntity> NetworkInterfaces { get; set; }
        public DbSet<AgentAdapterMacEntity> AgentAdapterMacs { get; set; }
        public DbSet<NetworkConnectionEntity> NetworkConnections { get; set; }
        public DbSet<NetworkSuggestionEntity> NetworkSuggestions { get; set; }
        public DbSet<NetworkRouterPositionEntity> NetworkRouterPositions { get; set; }

        // 3rd Party Connectors (docs/connectors-plan.md §10): connectors, callers, grants, a script connector's target and arguments, calls.
        public DbSet<ConnectorEntity> Connectors { get; set; }
        public DbSet<ConnectorCallerEntity> ConnectorCallers { get; set; }
        public DbSet<ConnectorGrantEntity> ConnectorGrants { get; set; }
        public DbSet<ConnectorScriptEntity> ConnectorScripts { get; set; }
        public DbSet<ConnectorArgumentEntity> ConnectorArguments { get; set; }
        public DbSet<ConnectorCallEntity> ConnectorCalls { get; set; }

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

        /// <summary>The Pushover priorities, one row (docs/cookie-object-notes-plan.md §5.1).</summary>
        public DbSet<NotificationSettingsEntity> NotificationSettings { get; set; }
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

        // The two outbound logs (docs/redmine-queue-outbound-plan.md §6).
        public DbSet<OutboundNotificationEntity> OutboundNotifications { get; set; }
        public DbSet<RedmineCallEntity> RedmineCalls { get; set; }

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

        // OS updates on Linux servers (docs/linux-os-updates-plan.md §5.2, §6, §7).
        public DbSet<OsUpdateRunEntity> OsUpdateRuns { get; set; }
        public DbSet<OsUpdateLogChunkEntity> OsUpdateLogChunks { get; set; }
        public DbSet<OsPendingPackageEntity> OsPendingPackages { get; set; }
        public DbSet<OsUpdateFindingEntity> OsUpdateFindings { get; set; }
        public DbSet<OsAdvisorySourceEntity> OsAdvisorySources { get; set; }
        public DbSet<OsFixEntity> OsFixes { get; set; }
        public DbSet<OsCveRiskEntity> OsCveRisks { get; set; }
        public DbSet<OsPackageChangelogEntity> OsPackageChangelogs { get; set; }
        public DbSet<OsAdvisoryItemEntity> OsAdvisoryItems { get; set; }

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
        public DbSet<KbPageLikeEntity> KbPageLikes { get; set; }
        public DbSet<KbPageUseEntity> KbPageUses { get; set; }
        public DbSet<UserNotificationPreferenceEntity> UserNotificationPreferences { get; set; }
        public DbSet<UserNotificationEntity> UserNotifications { get; set; }
        public DbSet<KbAiQuestionEntity> KbAiQuestions { get; set; }

        // Uploaded files (docs/locations-knowledge-plan.md §5.5, §8.2): their metadata; the bytes are in the files folder.
        public DbSet<FileEntity> Files { get; set; }
        public DbSet<FileUseEntity> FileUses { get; set; }

        // Responsibilities (docs/responsibilities-plan.md §10): who owns what, its links, the global people, the history.
        public DbSet<ResponsibilityEntity> Responsibilities { get; set; }
        public DbSet<ResponsibilityLinkEntity> ResponsibilityLinks { get; set; }
        public DbSet<ResponsibilitySettingsEntity> ResponsibilitySettings { get; set; }
        public DbSet<ResponsibilityHistoryEntity> ResponsibilityHistory { get; set; }
        public DbSet<ResponsibilityRedmineEntity> ResponsibilityRedmine { get; set; }

        // Cookie (docs/cookie-plan.md §19.2): its settings' versions, its state, the case book, the duties and the outbox.
        public DbSet<AvailabilityTypeEntity> AvailabilityTypes { get; set; }
        public DbSet<AvailabilityEntryEntity> AvailabilityEntries { get; set; }
        public DbSet<AvailabilityDayEntity> AvailabilityDays { get; set; }
        public DbSet<AvailabilitySettingsEntity> AvailabilitySettings { get; set; }
        public DbSet<CookieSettingEntity> CookieSettings { get; set; }
        public DbSet<CookieDecisionEntity> CookieDecisions { get; set; }
        public DbSet<CookieInstructionEntity> CookieInstructions { get; set; }
        public DbSet<CookieRuntimeEntity> CookieRuntime { get; set; }
        public DbSet<CookieCaseEntity> CookieCases { get; set; }

        /// <summary>Cookie's notes on hosts and sensors (docs/cookie-object-notes-plan.md §3.1).</summary>
        public DbSet<CookieObjectNoteEntity> CookieObjectNotes { get; set; }
        public DbSet<CookieCaseEntryEntity> CookieCaseEntries { get; set; }
        public DbSet<CookieDutyEntity> CookieDuties { get; set; }
        public DbSet<CookieOutboxEntity> CookieOutbox { get; set; }
        public DbSet<CookieRoundEntity> CookieRounds { get; set; }
        public DbSet<CookieTraceEntity> CookieTrace { get; set; }
        public DbSet<CookieMessageEntity> CookieMessages { get; set; }
        public DbSet<CookiePseudonymEntity> CookiePseudonyms { get; set; }

        // The lounge (docs/lounge-plan.md §12): one conversation, its scenes and log, the voices' memories and convictions, its state.
        public DbSet<LoungeMessageEntity> LoungeMessages { get; set; }
        public DbSet<LoungeSceneEntity> LoungeScenes { get; set; }
        public DbSet<LoungeStepEntity> LoungeSteps { get; set; }
        public DbSet<LoungeMemoryEntity> LoungeMemories { get; set; }
        public DbSet<LoungeOpinionEntity> LoungeOpinions { get; set; }
        public DbSet<LoungeRuntimeEntity> LoungeRuntime { get; set; }

        // The access watch (docs/cookie-access-plan.md §10.1)
        public DbSet<AccessReviewEntity> AccessReviews { get; set; }
        public DbSet<CookieAwayCommentEntity> CookieAwayComments { get; set; }
        public DbSet<AccessKnownEntity> AccessKnown { get; set; }

        // The MCP server (docs/mcp-plan.md §11.1)
        public DbSet<McpTokenEntity> McpTokens { get; set; }
        public DbSet<McpLogEntity> McpLog { get; set; }

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
            modelBuilder.Entity<UserEntity>().HasIndex(u => u.ShortCode).IsUnique();
            // One person per Redmine user (docs/cookie-redmine-tab-plan.md §4.1); unlinked ones are null, which PostgreSQL never counts as equal.
            modelBuilder.Entity<UserEntity>().HasIndex(u => u.RedmineUserId).IsUnique();
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

            // SSL certificate sensors (docs/ssl-certificate-plan.md §4): the row and the versions go with the sensor; a private CA
            // in use can't be deleted; the version pointers fall back to null when retention takes a version (it never takes those).
            modelBuilder.Entity<CertificateSensorEntity>(e =>
            {
                e.HasOne(c => c.Sensor).WithOne().HasForeignKey<CertificateSensorEntity>(c => c.SensorId).OnDelete(DeleteBehavior.Cascade);
                e.Property(c => c.Trust).HasConversion<string>().HasMaxLength(16);
                e.Property(c => c.IssuerMatch).HasConversion<string>().HasMaxLength(16);
                e.HasOne<CertificateVersionEntity>().WithMany().HasForeignKey(c => c.CurrentVersionId).OnDelete(DeleteBehavior.SetNull);
                e.HasOne<CertificateVersionEntity>().WithMany().HasForeignKey(c => c.AcceptedVersionId).OnDelete(DeleteBehavior.SetNull);
                e.HasOne<CertificateVersionEntity>().WithMany().HasForeignKey(c => c.AnnouncedVersionId).OnDelete(DeleteBehavior.SetNull);
                e.HasMany(c => c.Authorities).WithOne().HasForeignKey(a => a.SensorId).OnDelete(DeleteBehavior.Cascade);
                e.HasIndex(c => c.NextCheckAt);
            });
            modelBuilder.Entity<CertificateVersionEntity>(e =>
            {
                e.HasOne<SensorEntity>().WithMany().HasForeignKey(v => v.SensorId).OnDelete(DeleteBehavior.Cascade);
                e.HasIndex(v => new { v.SensorId, v.Sha256 }).IsUnique();
                e.HasIndex(v => new { v.SensorId, v.LastSeenAt });
            });
            modelBuilder.Entity<CertificateAuthorityEntity>(e =>
            {
                e.HasIndex(a => a.Name).IsUnique();
            });
            modelBuilder.Entity<CertificateSensorAuthorityEntity>(e =>
            {
                e.HasKey(a => new { a.SensorId, a.AuthorityId });
                e.HasOne(a => a.Authority).WithMany().HasForeignKey(a => a.AuthorityId).OnDelete(DeleteBehavior.Restrict);
            });

            // Advanced sensors (docs/advanced-sensor-plan.md §5): everything goes with the sensor; the latest report's pointer falls
            // back to null when a report is deleted; a field's samples go with the field.
            modelBuilder.Entity<AdvancedSensorEntity>(e =>
            {
                e.HasOne(a => a.Sensor).WithOne().HasForeignKey<AdvancedSensorEntity>(a => a.SensorId).OnDelete(DeleteBehavior.Cascade);
                e.HasOne<AdvancedReportEntity>().WithMany().HasForeignKey(a => a.LastReportId).OnDelete(DeleteBehavior.SetNull);
                e.HasIndex(a => a.ReevaluateRequestedAt).HasFilter("\"ReevaluateRequestedAt\" IS NOT NULL");
            });
            modelBuilder.Entity<AdvancedRuleEntity>(e =>
            {
                e.HasOne<SensorEntity>().WithMany().HasForeignKey(r => r.SensorId).OnDelete(DeleteBehavior.Cascade);
                e.Property(r => r.Operator).HasConversion<string>().HasMaxLength(24);
                e.Property(r => r.Quantifier).HasConversion<string>().HasMaxLength(8);
                e.Property(r => r.WhenMissing).HasConversion<string>().HasMaxLength(8);
                e.Property(r => r.Severity).HasConversion<string>().HasMaxLength(8);
                e.HasIndex(r => new { r.SensorId, r.Order });
            });
            modelBuilder.Entity<AdvancedFieldEntity>(e =>
            {
                e.HasOne<SensorEntity>().WithMany().HasForeignKey(f => f.SensorId).OnDelete(DeleteBehavior.Cascade);
                e.Property(f => f.Format).HasConversion<string>().HasMaxLength(16);
                e.HasIndex(f => new { f.SensorId, f.Order });
            });
            modelBuilder.Entity<AdvancedReportEntity>(e =>
            {
                e.HasOne<SensorEntity>().WithMany().HasForeignKey(r => r.SensorId).OnDelete(DeleteBehavior.Cascade);
                e.Property(r => r.ErrorKind).HasConversion<string>().HasMaxLength(16);
                e.HasIndex(r => new { r.SensorId, r.ReceivedAt }).IsDescending(false, true);
                // The evaluator's queue, found without reading the history (§5.4).
                e.HasIndex(r => new { r.SensorId, r.ReceivedAt }, "IX_advanced_reports_queue").HasFilter("\"EvaluatedAt\" IS NULL");
            });
            modelBuilder.Entity<AdvancedSampleEntity>(e =>
            {
                e.HasKey(x => new { x.SensorId, x.FieldId, x.Minute });
                e.HasOne<SensorEntity>().WithMany().HasForeignKey(x => x.SensorId).OnDelete(DeleteBehavior.Cascade);
                e.HasOne<AdvancedFieldEntity>().WithMany().HasForeignKey(x => x.FieldId).OnDelete(DeleteBehavior.Cascade);
                e.HasIndex(x => x.Minute);
            });

            // 3rd Party Connectors (docs/connectors-plan.md §10): the enums as text; the target and the arguments go with their
            // connector, the grants with either side; the calls outlive their connector and caller, and their run.
            modelBuilder.Entity<ConnectorEntity>(e =>
            {
                e.Property(c => c.Type).HasConversion<string>().HasMaxLength(32);
                e.Property(c => c.Mode).HasConversion<string>().HasMaxLength(16);
                e.Property(c => c.ReviewFallback).HasConversion<string>().HasMaxLength(16);
                e.HasIndex(c => c.Name).IsUnique();
                e.HasIndex(c => c.Slug).IsUnique();
                e.HasOne(c => c.Script).WithOne().HasForeignKey<ConnectorScriptEntity>(s => s.ConnectorId).OnDelete(DeleteBehavior.Cascade);
                e.HasMany(c => c.Arguments).WithOne().HasForeignKey(a => a.ConnectorId).OnDelete(DeleteBehavior.Cascade);
            });
            modelBuilder.Entity<ConnectorCallerEntity>(e =>
            {
                e.HasIndex(c => c.Name).IsUnique();
                e.HasIndex(c => c.KeyHash).IsUnique();
            });
            modelBuilder.Entity<ConnectorGrantEntity>(e =>
            {
                e.HasKey(g => new { g.CallerId, g.ConnectorId });
                e.HasOne(g => g.Caller).WithMany(c => c.Grants).HasForeignKey(g => g.CallerId).OnDelete(DeleteBehavior.Cascade);
                e.HasOne(g => g.Connector).WithMany(c => c.Grants).HasForeignKey(g => g.ConnectorId).OnDelete(DeleteBehavior.Cascade);
                e.HasIndex(g => g.ConnectorId);
            });
            modelBuilder.Entity<ConnectorScriptEntity>(e =>
            {
                e.Property(s => s.RunAsKind).HasConversion<string>().HasMaxLength(16);
                e.Property(s => s.ParseState).HasConversion<string>().HasMaxLength(16);
                e.HasIndex(s => s.Hash);
            });
            modelBuilder.Entity<ConnectorArgumentEntity>(e =>
            {
                e.Property(a => a.Mode).HasConversion<string>().HasMaxLength(16);
                e.HasIndex(a => new { a.ConnectorId, a.Name }).IsUnique();
            });
            modelBuilder.Entity<ConnectorCallEntity>(e =>
            {
                e.Property(c => c.State).HasConversion<string>().HasMaxLength(16);
                e.Property(c => c.Mode).HasConversion<string>().HasMaxLength(16);
                e.HasOne<ConnectorEntity>().WithMany().HasForeignKey(c => c.ConnectorId).OnDelete(DeleteBehavior.SetNull);
                e.HasOne<ConnectorCallerEntity>().WithMany().HasForeignKey(c => c.CallerId).OnDelete(DeleteBehavior.SetNull);
                e.HasOne<AgentScriptRunEntity>().WithMany().HasForeignKey(c => c.RunId).OnDelete(DeleteBehavior.SetNull);
                // The worker's queue (§10.5): small however long the history is.
                e.HasIndex(c => c.ReceivedAt, "IX_connector_calls_active").HasFilter("\"State\" IN ('Queued', 'Running')");
                e.HasIndex(c => new { c.ConnectorId, c.ReceivedAt }).IsDescending(false, true);
                e.HasIndex(c => new { c.CallerId, c.ReceivedAt }).IsDescending(false, true);
                e.HasIndex(c => new { c.CallerId, c.ConnectorId, c.IdempotencyKey }).IsUnique().HasFilter("\"IdempotencyKey\" IS NOT NULL");
                e.HasIndex(c => c.ReceivedAt);
                e.HasIndex(c => c.RunId);
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
                // The access watch's history of PNMT's sign-ins (docs/cookie-access-plan.md §4.3).
                e.HasIndex(l => new { l.EventType, l.Username, l.CreatedAt });
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

            // The outbound logs (docs/redmine-queue-outbound-plan.md §6.2): newest first, by channel, outcome and job.
            modelBuilder.Entity<OutboundNotificationEntity>(e =>
            {
                e.HasIndex(n => new { n.Channel, n.Id });
                e.HasIndex(n => new { n.Outcome, n.Id });
            });
            modelBuilder.Entity<RedmineCallEntity>(e =>
            {
                e.HasIndex(c => c.JobId);
                e.HasIndex(c => new { c.Outcome, c.Id });
            });

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
                // The access watch's history (docs/cookie-access-plan.md §4.4): one account's logins on one target.
                e.HasIndex(l => new { l.SensorId, l.User, l.At });
                // Its signals across targets: a source's failures and fan-out.
                e.HasIndex(l => new { l.SourceAddress, l.At });
            });
            // The away watch's comments (docs/cookie-redmine-tab-plan.md §7): one row per journal, going with its case.
            modelBuilder.Entity<CookieAwayCommentEntity>(e =>
            {
                e.Property(a => a.State).HasConversion<string>().HasMaxLength(16);
                e.HasOne<CookieCaseEntity>().WithMany().HasForeignKey(a => a.CaseId).OnDelete(DeleteBehavior.Cascade);
                e.HasIndex(a => a.JournalId).IsUnique();
                e.HasIndex(a => new { a.State, a.CreatedAt });
                e.HasIndex(a => new { a.IssueId, a.NotifiedAt });
            });
            modelBuilder.Entity<AccessReviewEntity>(e =>
            {
                e.Property(r => r.Target).HasConversion<string>().HasMaxLength(16);
                e.Property(r => r.State).HasConversion<string>().HasMaxLength(16);
                e.HasOne<SensorEntity>().WithMany().HasForeignKey(r => r.SensorId).OnDelete(DeleteBehavior.SetNull);
                e.HasOne<AgentLoginEntity>().WithMany().HasForeignKey(r => r.AgentLoginId).OnDelete(DeleteBehavior.SetNull);
                e.HasOne<CookieCaseEntity>().WithMany().HasForeignKey(r => r.CaseId).OnDelete(DeleteBehavior.SetNull);
                e.HasIndex(r => new { r.State, r.CreatedAt });
                e.HasIndex(r => r.CaseId);
                e.HasIndex(r => r.AgentLoginId);
                e.HasIndex(r => r.AuditLogId);
                e.HasIndex(r => new { r.Source, r.At });
                e.HasIndex(r => r.TokenHash).IsUnique().HasFilter("\"TokenHash\" IS NOT NULL");
            });
            modelBuilder.Entity<AccessKnownEntity>(e =>
            {
                e.Property(k => k.Target).HasConversion<string>().HasMaxLength(16);
                e.HasIndex(k => k.EndedAt);
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
                // docs/connectors-plan.md §9.2, §10.5: one run per script and server; the runs of a connector.
                e.HasIndex(r => new { r.SensorId, r.ScriptName }, "IX_agent_script_runs_unfinished").HasFilter("\"State\" IN (0, 1)");
                e.HasIndex(r => r.ConnectorId);
            });
            // OS updates (docs/linux-os-updates-plan.md §5.2): runs, logs, pending packages and findings go with their server; the
            // sources' data goes with its source.
            modelBuilder.Entity<OsUpdateRunEntity>(e =>
            {
                e.HasOne<SensorEntity>().WithMany().HasForeignKey(r => r.SensorId).OnDelete(DeleteBehavior.Cascade);
                e.HasIndex(r => new { r.SensorId, r.Kind, r.RequestedAt });
                e.HasIndex(r => new { r.SensorId, r.ExternalId }).IsUnique().HasFilter("\"ExternalId\" IS NOT NULL");
                e.HasIndex(r => r.State);
                e.Ignore(r => r.Finished);
            });
            modelBuilder.Entity<OsUpdateLogChunkEntity>(e =>
            {
                e.HasKey(c => new { c.RunId, c.Offset });
                e.HasOne<OsUpdateRunEntity>().WithMany().HasForeignKey(c => c.RunId).OnDelete(DeleteBehavior.Cascade);
            });
            modelBuilder.Entity<OsPendingPackageEntity>(e =>
            {
                e.HasKey(p => new { p.SensorId, p.Name, p.Arch });
                e.HasOne<SensorEntity>().WithMany().HasForeignKey(p => p.SensorId).OnDelete(DeleteBehavior.Cascade);
                e.HasIndex(p => new { p.Source, p.SourceVersion });
            });
            modelBuilder.Entity<OsUpdateFindingEntity>(e =>
            {
                e.HasOne<SensorEntity>().WithMany().HasForeignKey(f => f.SensorId).OnDelete(DeleteBehavior.Cascade);
                e.HasIndex(f => new { f.SensorId, f.SourcePackage, f.Cve }, "IX_os_update_findings_open").IsUnique().HasFilter("\"ResolvedAt\" IS NULL");
                e.HasIndex(f => f.ResolvedAt);
            });
            modelBuilder.Entity<OsAdvisorySourceEntity>(e => e.HasIndex(s => s.Kind));
            modelBuilder.Entity<OsFixEntity>(e =>
            {
                e.HasKey(f => new { f.SourceId, f.Distribution, f.SourcePackage, f.Cve });
                e.HasOne<OsAdvisorySourceEntity>().WithMany().HasForeignKey(f => f.SourceId).OnDelete(DeleteBehavior.Cascade);
                e.HasIndex(f => new { f.Distribution, f.SourcePackage });
            });
            modelBuilder.Entity<OsPackageChangelogEntity>(e =>
            {
                e.HasKey(c => new { c.Distribution, c.SourcePackage, c.Version });
                e.HasIndex(c => c.ReceivedAt);
            });
            modelBuilder.Entity<OsAdvisoryItemEntity>(e =>
            {
                e.HasOne<OsAdvisorySourceEntity>().WithMany().HasForeignKey(i => i.SourceId).OnDelete(DeleteBehavior.Cascade);
                e.HasIndex(i => new { i.SourceId, i.Guid, i.Hash }).IsUnique();
                e.HasIndex(i => i.FirstSeenAt);
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
            // A rule's user or group (docs/notification-recipients-plan.md §3.1): deleting them leaves the rule, which then shows
            // "removed" and reaches nobody until it is changed.
            modelBuilder.Entity<NotificationRuleEntity>(e =>
            {
                e.HasOne(r => r.User).WithMany().HasForeignKey(r => r.UserId).OnDelete(DeleteBehavior.SetNull);
                e.HasOne(r => r.Group).WithMany().HasForeignKey(r => r.GroupId).OnDelete(DeleteBehavior.SetNull);
            });

            ConfigureLocations(modelBuilder);
            ConfigureNetworkMap(modelBuilder);
            ConfigureKnowledgeBase(modelBuilder);
            ConfigureUserNotifications(modelBuilder);
            ConfigureFiles(modelBuilder);
            ConfigureResponsibilities(modelBuilder);
            modelBuilder.Entity<ZabbixProblemHistoryEntity>(e =>
            {
                e.HasIndex(h => h.StartedAt);
                e.HasIndex(h => h.ResolvedAt);
            });
            ConfigureCookie(modelBuilder);
            ConfigureAvailability(modelBuilder);

            base.OnModelCreating(modelBuilder);
        }

        /// <summary>
        /// Locations (docs/locations-knowledge-plan.md §8.2): a parent can't be deleted while it has sub-locations (§3.3);
        /// deleting a location unassigns its hosts and takes its values and history along.
        /// </summary>
        /// <summary>
        /// The network map (docs/network-map-plan.md §9.2). A router's scan data goes with its sensor; a connection with either end's
        /// host or router; runs keep their history when a router or location goes.
        /// </summary>
        private static void ConfigureNetworkMap(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<NetworkScanEntity>(e =>
            {
                e.HasOne<LocationEntity>().WithMany().HasForeignKey(s => s.LocationId).OnDelete(DeleteBehavior.SetNull);
                e.HasOne<SensorEntity>().WithMany().HasForeignKey(s => s.SensorId).OnDelete(DeleteBehavior.SetNull);
                e.HasIndex(s => new { s.State, s.CreatedAt });
                e.HasIndex(s => s.CreatedAt);
            });
            modelBuilder.Entity<NetworkScanItemEntity>(e =>
            {
                e.HasOne<NetworkScanEntity>().WithMany().HasForeignKey(i => i.ScanId).OnDelete(DeleteBehavior.Cascade);
                e.HasOne<SensorEntity>().WithMany().HasForeignKey(i => i.SensorId).OnDelete(DeleteBehavior.SetNull);
                e.HasIndex(i => new { i.ScanId, i.Position });
                e.HasIndex(i => new { i.State, i.SensorId });
                e.HasIndex(i => new { i.SensorId, i.FinishedAt });
            });
            modelBuilder.Entity<NetworkRouterSnapshotEntity>(e =>
            {
                e.HasOne<SensorEntity>().WithMany().HasForeignKey(s => s.SensorId).OnDelete(DeleteBehavior.Cascade);
            });
            modelBuilder.Entity<NetworkRouterPortEntity>(e =>
            {
                e.HasKey(p => new { p.SensorId, p.Name });
                e.HasOne<SensorEntity>().WithMany().HasForeignKey(p => p.SensorId).OnDelete(DeleteBehavior.Cascade);
                e.HasIndex(p => p.Mac);
            });
            modelBuilder.Entity<NetworkObservationEntity>(e =>
            {
                e.HasOne<SensorEntity>().WithMany().HasForeignKey(o => o.SensorId).OnDelete(DeleteBehavior.Cascade);
                e.HasIndex(o => new { o.SensorId, o.Source });
                e.HasIndex(o => o.Mac);
            });
            modelBuilder.Entity<NetworkInterfaceEntity>(e =>
            {
                e.HasOne<HostEntity>().WithMany().HasForeignKey(i => i.AssignedHostId).OnDelete(DeleteBehavior.SetNull);
                e.HasIndex(i => i.MatchHostId);
                e.HasIndex(i => i.AttachedSensorId);
                e.HasIndex(i => i.LastSeenAt);
            });
            modelBuilder.Entity<AgentAdapterMacEntity>(e =>
            {
                e.HasKey(m => new { m.SensorId, m.Mac, m.Adapter });
                e.HasOne<SensorEntity>().WithMany().HasForeignKey(m => m.SensorId).OnDelete(DeleteBehavior.Cascade);
                e.HasIndex(m => m.Mac);
            });
            modelBuilder.Entity<NetworkConnectionEntity>(e =>
            {
                e.HasIndex(c => c.Key).IsUnique();
                e.HasOne<HostEntity>().WithMany().HasForeignKey(c => c.AHostId).OnDelete(DeleteBehavior.Cascade);
                e.HasOne<HostEntity>().WithMany().HasForeignKey(c => c.BHostId).OnDelete(DeleteBehavior.Cascade);
                e.HasOne<SensorEntity>().WithMany().HasForeignKey(c => c.ASensorId).OnDelete(DeleteBehavior.Cascade);
                e.HasOne<SensorEntity>().WithMany().HasForeignKey(c => c.BSensorId).OnDelete(DeleteBehavior.Cascade);
                e.HasIndex(c => c.ASensorId);
                e.HasIndex(c => c.BSensorId);
                e.HasIndex(c => c.BHostId);
            });
            modelBuilder.Entity<NetworkRouterPositionEntity>(e =>
            {
                e.HasOne<SensorEntity>().WithMany().HasForeignKey(p => p.SensorId).OnDelete(DeleteBehavior.Cascade);
            });
            modelBuilder.Entity<NetworkSuggestionEntity>(e =>
            {
                e.HasIndex(s => s.Key).IsUnique();
                e.HasIndex(s => new { s.State, s.Confidence });
                e.HasOne<NetworkConnectionEntity>().WithMany().HasForeignKey(s => s.ConnectionId).OnDelete(DeleteBehavior.SetNull);
                e.HasOne<HostEntity>().WithMany().HasForeignKey(s => s.AHostId).OnDelete(DeleteBehavior.Cascade);
                e.HasOne<HostEntity>().WithMany().HasForeignKey(s => s.BHostId).OnDelete(DeleteBehavior.Cascade);
                e.HasOne<SensorEntity>().WithMany().HasForeignKey(s => s.ASensorId).OnDelete(DeleteBehavior.Cascade);
                e.HasOne<SensorEntity>().WithMany().HasForeignKey(s => s.BSensorId).OnDelete(DeleteBehavior.Cascade);
            });
        }

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
                // Most liked and most used of all time (docs/knowledge-base-extensions-plan.md §9).
                e.HasIndex(p => p.Likes);
                e.HasIndex(p => p.Uses);
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
                e.ToTable(t =>
                {
                    t.HasCheckConstraint("CK_kb_links_one_target",
                        "num_nonnulls(\"LocationId\", \"HostId\", \"SensorId\", \"TargetPageId\") = 1");
                    // A page link joins two pages (docs/knowledge-base-extensions-plan.md §7).
                    t.HasCheckConstraint("CK_kb_links_not_itself", "\"TargetPageId\" IS NULL OR \"TargetPageId\" <> \"PageId\"");
                });
                e.HasOne<LocationEntity>().WithMany().HasForeignKey(l => l.LocationId).OnDelete(DeleteBehavior.Cascade);
                e.HasOne<HostEntity>().WithMany().HasForeignKey(l => l.HostId).OnDelete(DeleteBehavior.Cascade);
                e.HasOne<SensorEntity>().WithMany().HasForeignKey(l => l.SensorId).OnDelete(DeleteBehavior.Cascade);
                // Deleting either page removes both rows of their link: this one through its PageId, the mirror through this.
                e.HasOne<KbPageEntity>().WithMany().HasForeignKey(l => l.TargetPageId).OnDelete(DeleteBehavior.Cascade);
                // Once per page and target. The target leads, so the same indexes find a target's pages (GET /kb/related)
                // and serve the cascades; the page's links have an index of their own.
                e.HasIndex(l => new { l.LocationId, l.PageId }).IsUnique().HasFilter("\"LocationId\" IS NOT NULL");
                e.HasIndex(l => new { l.HostId, l.PageId }).IsUnique().HasFilter("\"HostId\" IS NOT NULL");
                e.HasIndex(l => new { l.SensorId, l.PageId }).IsUnique().HasFilter("\"SensorId\" IS NOT NULL");
                e.HasIndex(l => new { l.TargetPageId, l.PageId }).IsUnique().HasFilter("\"TargetPageId\" IS NOT NULL");
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
            modelBuilder.Entity<KbPageLikeEntity>(e =>
            {
                // Once per person and page; the page's leads, for its likes and the 30 days.
                e.HasIndex(l => new { l.PageId, l.UserId }).IsUnique();
                e.HasOne<KbPageEntity>().WithMany().HasForeignKey(l => l.PageId).OnDelete(DeleteBehavior.Cascade);
                e.HasIndex(l => l.At);
            });
            modelBuilder.Entity<KbPageUseEntity>(e =>
            {
                e.HasOne<KbPageEntity>().WithMany().HasForeignKey(u => u.PageId).OnDelete(DeleteBehavior.Cascade);
                e.HasIndex(u => new { u.PageId, u.At });
                e.HasIndex(u => new { u.PageId, u.UserId, u.At });
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
        /// Notifications to people (docs/knowledge-base-extensions-plan.md §10): a person's choices and their queue go with them.
        /// </summary>
        private static void ConfigureUserNotifications(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<UserNotificationPreferenceEntity>(e =>
            {
                e.HasKey(p => new { p.UserId, p.Kind, p.Channel });
                e.HasOne<UserEntity>().WithMany().HasForeignKey(p => p.UserId).OnDelete(DeleteBehavior.Cascade);
            });
            modelBuilder.Entity<UserNotificationEntity>(e =>
            {
                e.Property(n => n.Id).UseIdentityByDefaultColumn();
                e.HasOne<UserEntity>().WithMany().HasForeignKey(n => n.UserId).OnDelete(DeleteBehavior.Cascade);
                // The due batches; a person's e-mails today (the daily limit); cancelling what an undone like made.
                e.HasIndex(n => new { n.State, n.DueAt });
                e.HasIndex(n => new { n.UserId, n.SentAt });
                e.HasIndex(n => n.SourceId);
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
                e.ToTable(t => t.HasCheckConstraint("CK_file_uses_one_owner", "num_nonnulls(\"PageId\", \"LocationId\", \"ResponsibilityId\") = 1"));
                e.HasOne<FileEntity>().WithMany().HasForeignKey(u => u.FileId).OnDelete(DeleteBehavior.Cascade);
                e.HasOne<KbPageEntity>().WithMany().HasForeignKey(u => u.PageId).OnDelete(DeleteBehavior.Cascade);
                e.HasOne<LocationEntity>().WithMany().HasForeignKey(u => u.LocationId).OnDelete(DeleteBehavior.Cascade);
                // A responsibility's description (docs/responsibilities-plan.md §10.4).
                e.HasOne<ResponsibilityEntity>().WithMany().HasForeignKey(u => u.ResponsibilityId).OnDelete(DeleteBehavior.Cascade);
                // Once per file and owner. The owner leads: a page's attachments (§6.1) and the cascades; where a file is
                // used, and whether at all, by FileId.
                e.HasIndex(u => new { u.PageId, u.FileId }).IsUnique().HasFilter("\"PageId\" IS NOT NULL");
                e.HasIndex(u => new { u.LocationId, u.FileId }).IsUnique().HasFilter("\"LocationId\" IS NOT NULL");
                e.HasIndex(u => new { u.ResponsibilityId, u.FileId }).IsUnique().HasFilter("\"ResponsibilityId\" IS NOT NULL");
                e.HasIndex(u => u.FileId);
            });
        }

        /// <summary>Cookie (docs/cookie-plan.md §19.2): enums as text, one open case per key, a duty once per case and key.</summary>
        /// <summary>
        /// Responsibilities (docs/responsibilities-plan.md §10): a deleted user's place becomes empty; a link goes with either
        /// side; the settings are one row, inserted here.
        /// </summary>
        private static void ConfigureResponsibilities(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<ResponsibilityEntity>(e =>
            {
                e.ToTable(t => t.HasCheckConstraint("CK_responsibilities_two_people",
                    "\"ResponsibleId\" IS NULL OR \"DeputyId\" IS NULL OR \"ResponsibleId\" <> \"DeputyId\""));
                e.HasIndex(r => r.NormalizedName).IsUnique();
                e.HasOne<UserEntity>().WithMany().HasForeignKey(r => r.ResponsibleId).OnDelete(DeleteBehavior.SetNull);
                e.HasOne<UserEntity>().WithMany().HasForeignKey(r => r.DeputyId).OnDelete(DeleteBehavior.SetNull);
            });
            modelBuilder.Entity<ResponsibilityLinkEntity>(e =>
            {
                e.ToTable(t => t.HasCheckConstraint("CK_responsibility_links_one_target", "num_nonnulls(\"PageId\", \"LocationId\", \"HostId\") = 1"));
                e.HasOne<ResponsibilityEntity>().WithMany().HasForeignKey(l => l.ResponsibilityId).OnDelete(DeleteBehavior.Cascade);
                e.HasOne<KbPageEntity>().WithMany().HasForeignKey(l => l.PageId).OnDelete(DeleteBehavior.Cascade);
                e.HasOne<LocationEntity>().WithMany().HasForeignKey(l => l.LocationId).OnDelete(DeleteBehavior.Cascade);
                e.HasOne<HostEntity>().WithMany().HasForeignKey(l => l.HostId).OnDelete(DeleteBehavior.Cascade);
                // Once per object and responsibility. The object leads, so the same indexes find an object's responsibilities
                // (the panels, §8) and serve the cascades; a responsibility's links have an index of their own.
                e.HasIndex(l => new { l.PageId, l.ResponsibilityId }).IsUnique().HasFilter("\"PageId\" IS NOT NULL");
                e.HasIndex(l => new { l.LocationId, l.ResponsibilityId }).IsUnique().HasFilter("\"LocationId\" IS NOT NULL");
                e.HasIndex(l => new { l.HostId, l.ResponsibilityId }).IsUnique().HasFilter("\"HostId\" IS NOT NULL");
                e.HasIndex(l => l.ResponsibilityId);
            });
            modelBuilder.Entity<ResponsibilitySettingsEntity>(e =>
            {
                e.ToTable(t =>
                {
                    t.HasCheckConstraint("CK_responsibility_settings_one_row", $"\"Id\" = {ResponsibilitySettingsEntity.SingletonId}");
                    t.HasCheckConstraint("CK_responsibility_settings_two_people",
                        "\"GlobalResponsibleId\" IS NULL OR \"GlobalDeputyId\" IS NULL OR \"GlobalResponsibleId\" <> \"GlobalDeputyId\"");
                });
                e.HasOne<UserEntity>().WithMany().HasForeignKey(s => s.GlobalResponsibleId).OnDelete(DeleteBehavior.SetNull);
                e.HasOne<UserEntity>().WithMany().HasForeignKey(s => s.GlobalDeputyId).OnDelete(DeleteBehavior.SetNull);
                // The row exists from the start, so a save is always an update (§5).
                e.HasData(new ResponsibilitySettingsEntity
                {
                    Id = ResponsibilitySettingsEntity.SingletonId,
                    UpdatedAt = new DateTime(2026, 11, 23, 0, 0, 0, DateTimeKind.Utc),
                });
            });
            modelBuilder.Entity<ResponsibilityHistoryEntity>(e =>
            {
                e.HasOne<ResponsibilityEntity>().WithMany().HasForeignKey(h => h.ResponsibilityId).OnDelete(DeleteBehavior.Cascade);
                e.HasIndex(h => new { h.ResponsibilityId, h.At });
            });
            // Redmine combinations (docs/responsibilities-plan.md §6): once per project and tracker, every tracker (null) included.
            modelBuilder.Entity<ResponsibilityRedmineEntity>(e =>
            {
                e.HasOne<ResponsibilityEntity>().WithMany().HasForeignKey(c => c.ResponsibilityId).OnDelete(DeleteBehavior.Cascade);
                e.HasOne<UserEntity>().WithMany().HasForeignKey(c => c.UserId).OnDelete(DeleteBehavior.SetNull);
                e.HasIndex(c => new { c.ProjectId, c.TrackerId }).IsUnique().AreNullsDistinct(false);
                e.HasIndex(c => c.ResponsibilityId);
                e.HasIndex(c => c.UserId);
            });
        }

        /// <summary>The availability plan (docs/cookie-plan.md §8.2): a cell per person and day, a row per day.</summary>
        private static void ConfigureAvailability(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<AvailabilityEntryEntity>(e =>
            {
                e.HasKey(x => new { x.UserId, x.Date });
                e.HasIndex(x => x.Date);
                e.HasOne<UserEntity>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
                e.HasOne<AvailabilityTypeEntity>().WithMany().HasForeignKey(x => x.TypeId).OnDelete(DeleteBehavior.Restrict);
            });
            modelBuilder.Entity<AvailabilityDayEntity>(e =>
            {
                e.HasOne<UserEntity>().WithMany().HasForeignKey(x => x.ResponsibleId).OnDelete(DeleteBehavior.SetNull);
                e.HasOne<UserEntity>().WithMany().HasForeignKey(x => x.FallbackId).OnDelete(DeleteBehavior.SetNull);
            });
        }

        private static void ConfigureCookie(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<CookieSettingEntity>().HasIndex(s => new { s.Section, s.Version }).IsUnique();
            // Package nine point five (docs/cookie-plan.md §21.2): the decision log, and mute, suppress and instructions.
            modelBuilder.Entity<CookieDecisionEntity>(e =>
            {
                e.HasOne<CookieCaseEntity>().WithMany().HasForeignKey(x => x.CaseId).OnDelete(DeleteBehavior.SetNull);
                e.HasIndex(x => x.At);
                e.HasIndex(x => new { x.CaseId, x.Kind, x.At });
                e.HasIndex(x => x.InboxDecisionId);
            });
            modelBuilder.Entity<CookieInstructionEntity>().HasIndex(x => new { x.Confirmed, x.EndedAt, x.Until });
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
            // Cookie's notes on hosts and sensors (docs/cookie-object-notes-plan.md §3.1): with their object, never the people's notes.
            modelBuilder.Entity<CookieObjectNoteEntity>(e =>
            {
                e.HasOne<HostEntity>().WithMany().HasForeignKey(x => x.HostId).OnDelete(DeleteBehavior.Cascade);
                e.HasOne<SensorEntity>().WithMany().HasForeignKey(x => x.SensorId).OnDelete(DeleteBehavior.Cascade);
                e.HasOne<CookieCaseEntity>().WithMany().HasForeignKey(x => x.CaseId).OnDelete(DeleteBehavior.SetNull);
                e.HasOne<CookieDecisionEntity>().WithMany().HasForeignKey(x => x.DecisionId).OnDelete(DeleteBehavior.SetNull);
                e.HasIndex(x => new { x.HostId, x.At });
                e.HasIndex(x => new { x.SensorId, x.At });
                e.ToTable(t => t.HasCheckConstraint("CK_cookie_object_notes_object", "(\"HostId\" IS NULL) <> (\"SensorId\" IS NULL)"));
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
            // The MCP server (docs/mcp-plan.md §4.2, §9.1): text for the enums, as in the audit log.
            modelBuilder.Entity<McpTokenEntity>(e =>
            {
                e.Property(t => t.Kind).HasConversion<string>().HasMaxLength(8);
                e.Property(t => t.EndedWhy).HasConversion<string>().HasMaxLength(16);
                e.HasOne<UserEntity>().WithMany().HasForeignKey(t => t.UserId).OnDelete(DeleteBehavior.Cascade);
                e.HasIndex(t => new { t.UserId, t.Kind }).IsUnique();
                e.HasIndex(t => t.Hash).IsUnique();
            });
            modelBuilder.Entity<McpLogEntity>(e =>
            {
                e.Property(l => l.Kind).HasConversion<string>().HasMaxLength(8);
                e.Property(l => l.Outcome).HasConversion<string>().HasMaxLength(16);
                e.HasIndex(l => l.At);
                e.HasIndex(l => new { l.UserId, l.Id });
                e.HasIndex(l => new { l.TokenId, l.Id });
                e.HasIndex(l => new { l.Tool, l.Id });
                e.HasIndex(l => l.CorrelationId);
            });
            modelBuilder.Entity<CookiePseudonymEntity>(e =>
            {
                e.HasIndex(p => new { p.Kind, p.Value }).IsUnique();
                e.HasIndex(p => p.Placeholder).IsUnique();
            });
            ConfigureLounge(modelBuilder);
        }

        /// <summary>The lounge (docs/lounge-plan.md §12): the conversation by time, comments by the message they are on, memories by voice.</summary>
        private static void ConfigureLounge(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<LoungeMessageEntity>(e =>
            {
                e.HasOne<UserEntity>().WithMany().HasForeignKey(m => m.UserId).OnDelete(DeleteBehavior.SetNull);
                e.HasOne<LoungeSceneEntity>().WithMany().HasForeignKey(m => m.SceneId).OnDelete(DeleteBehavior.SetNull);
                e.HasOne<CookieCaseEntity>().WithMany().HasForeignKey(m => m.CaseId).OnDelete(DeleteBehavior.SetNull);
                e.HasIndex(m => m.At);
                e.HasIndex(m => m.OnId);
                e.HasIndex(m => new { m.UserId, m.At });
                e.HasIndex(m => m.Kind).HasFilter("\"Kind\" = 'comment' AND \"TakenById\" IS NULL AND \"RefusedWhy\" IS NULL");
            });
            modelBuilder.Entity<LoungeSceneEntity>().HasIndex(s => s.StartedAt);
            modelBuilder.Entity<LoungeStepEntity>(e =>
            {
                e.HasOne<LoungeSceneEntity>().WithMany().HasForeignKey(s => s.SceneId).OnDelete(DeleteBehavior.Cascade);
                e.HasIndex(s => new { s.SceneId, s.Id });
                e.HasIndex(s => s.At);
            });
            modelBuilder.Entity<LoungeMemoryEntity>().HasIndex(m => new { m.Voice, m.Keepsake });
            modelBuilder.Entity<LoungeOpinionEntity>().HasIndex(o => new { o.Conviction, o.At });
            modelBuilder.Entity<LoungeRuntimeEntity>().Property(r => r.State).HasConversion<string>().HasMaxLength(16);
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
