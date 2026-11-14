using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PNMTD.Data.Migrations
{
    /// <inheritdoc />
    public partial class AuditLog : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // auth_log becomes audit_log and keeps its rows (docs/audit-redmine-zabbix-plan.md §3.2).
            migrationBuilder.RenameTable(name: "auth_log", newName: "audit_log");
            migrationBuilder.Sql("""ALTER TABLE audit_log RENAME CONSTRAINT "PK_auth_log" TO "PK_audit_log";""");
            migrationBuilder.RenameIndex(name: "IX_auth_log_CreatedAt", table: "audit_log", newName: "IX_audit_log_CreatedAt");
            migrationBuilder.RenameIndex(name: "IX_auth_log_UserId_CreatedAt", table: "audit_log", newName: "IX_audit_log_UserId_CreatedAt");

            migrationBuilder.AddColumn<string>(name: "Category", table: "audit_log", type: "character varying(32)", maxLength: 32, nullable: false, defaultValue: "System");
            migrationBuilder.AddColumn<string>(name: "Actor", table: "audit_log", type: "character varying(32)", maxLength: 32, nullable: false, defaultValue: "User");
            migrationBuilder.AddColumn<string>(name: "ActorRef", table: "audit_log", type: "character varying(200)", maxLength: 200, nullable: true);
            migrationBuilder.AddColumn<string>(name: "Outcome", table: "audit_log", type: "character varying(16)", maxLength: 16, nullable: false, defaultValue: "Succeeded");
            migrationBuilder.AddColumn<string>(name: "Error", table: "audit_log", type: "character varying(200)", maxLength: 200, nullable: true);
            migrationBuilder.AddColumn<string>(name: "TargetType", table: "audit_log", type: "character varying(64)", maxLength: 64, nullable: true);
            migrationBuilder.AddColumn<string>(name: "TargetId", table: "audit_log", type: "character varying(64)", maxLength: 64, nullable: true);
            migrationBuilder.AddColumn<string>(name: "TargetName", table: "audit_log", type: "character varying(500)", maxLength: 500, nullable: true);
            migrationBuilder.AddColumn<List<Guid>>(name: "Subjects", table: "audit_log", type: "uuid[]", nullable: false, defaultValueSql: "'{}'::uuid[]");
            migrationBuilder.AddColumn<string>(name: "Changes", table: "audit_log", type: "jsonb", nullable: true);
            migrationBuilder.AddColumn<string>(name: "Request", table: "audit_log", type: "character varying(300)", maxLength: 300, nullable: true);
            migrationBuilder.AddColumn<string>(name: "CorrelationId", table: "audit_log", type: "character varying(100)", maxLength: 100, nullable: true);

            // The old rows: their category from the catalog, their outcome from the type, the actor from Detail's prefix
            // ("cli: …", "agent: …"), which goes; an account's events are about the account (UserId was the account's).
            migrationBuilder.Sql("""
                UPDATE audit_log SET "Category" = CASE
                    WHEN "EventType" IN ('AgentDownloadsFetched', 'AgentJoinFailed', 'AgentJoinTokenCreated', 'AgentJoinTokenRevoked', 'AgentJoined', 'AgentRevoked', 'AgentScriptChanged', 'AgentScriptRun', 'AgentSettingsChanged', 'WindowsSettingsChanged') THEN 'Agents'
                    WHEN "EventType" IN ('AiAnalysisStarted', 'AiBackfillStarted', 'AiWorkCancelled', 'AiWorkRetried', 'DebugAiTested') THEN 'Ai'
                    WHEN "EventType" IN ('LockedOut', 'LoginFailed', 'LoginSucceeded', 'Logout', 'PasswordChanged', 'SessionsRevoked', 'TotpDisabled', 'TotpEnabled', 'TotpFailed') THEN 'Auth'
                    WHEN "EventType" IN ('DiagnosticsAssignmentChanged', 'DiagnosticsSetChanged') THEN 'Diagnostics'
                    WHEN "EventType" IN ('FileDeleted') THEN 'Files'
                    WHEN "EventType" IN ('KbPageDeleted', 'KbPageSensitivityChanged', 'KbRevisionRedacted', 'KbTagDeleted') THEN 'Knowledge'
                    WHEN "EventType" IN ('LocationDeleted', 'LocationFieldDeleted') THEN 'Locations'
                    WHEN "EventType" IN ('MikrotikBackupDeleted', 'MikrotikBackupDownloaded', 'MikrotikBackupEncryptionChanged', 'MikrotikBackupKeyRetired', 'MikrotikBackupKeySet', 'MikrotikCommandCancelled', 'MikrotikCommandRedacted', 'MikrotikCommandStarted', 'MikrotikDiagnosticsChanged', 'MikrotikHostKeyAccepted', 'MikrotikJoinScriptViewed', 'MikrotikManagementAccessChanged', 'MikrotikSecretsChanged', 'MikrotikSshKeyRetired', 'MikrotikSshKeyRotated', 'MikrotikUpdateCancelled', 'MikrotikUpdateStarted') THEN 'Mikrotik'
                    WHEN "EventType" IN ('SshKeyChanged', 'SshKeyDeleted', 'SshKeyRegistered', 'SshKeyRemoved', 'SshKeyRetired') THEN 'SshKeys'
                    WHEN "EventType" IN ('DebugNotificationSent') THEN 'System'
                    WHEN "EventType" IN ('UdpCounterReset', 'UdpFirmwareDeleted', 'UdpFirmwareUploaded', 'UdpKeyChanged', 'UdpSessionReset', 'UdpUpdateCancelled', 'UdpUpdateStarted') THEN 'Udp'
                    WHEN "EventType" IN ('PasswordReset', 'RoleChanged', 'TotpReset', 'UserCreated', 'UserDeleted', 'UserUnlocked', 'UserUpdated') THEN 'Users'
                    WHEN "EventType" IN ('WireGuardKeysRegenerated', 'WireGuardNetworkChanged', 'WireGuardTunnelChanged', 'WireGuardTunnelCreated', 'WireGuardTunnelDeleted', 'WireGuardTunnelDownloaded') THEN 'WireGuard'
                    ELSE 'System' END;
                UPDATE audit_log SET "Outcome" = 'Failed' WHERE "EventType" IN ('LoginFailed', 'TotpFailed', 'LockedOut', 'AgentJoinFailed');
                UPDATE audit_log SET "Actor" = 'Cli', "Detail" = NULLIF(regexp_replace("Detail", '^cli(: )?', ''), '') WHERE "Detail" = 'cli' OR "Detail" LIKE 'cli: %';
                UPDATE audit_log SET "Actor" = 'Agent', "Detail" = substring("Detail" from 8) WHERE "Detail" LIKE 'agent: %';
                UPDATE audit_log SET "Actor" = 'Agent' WHERE "Detail" LIKE 'agent %';
                UPDATE audit_log SET "TargetType" = 'User', "TargetId" = "UserId"::text, "TargetName" = "Username", "Subjects" = ARRAY["UserId"]
                    WHERE "UserId" IS NOT NULL AND "Category" IN ('Users', 'Auth')
                        AND "EventType" NOT IN ('LoginSucceeded', 'LoginFailed', 'LockedOut', 'TotpFailed', 'Logout');
                """);

            migrationBuilder.CreateIndex(name: "IX_audit_log_EventType_CreatedAt", table: "audit_log", columns: new[] { "EventType", "CreatedAt" });
            migrationBuilder.CreateIndex(name: "IX_audit_log_Subjects", table: "audit_log", column: "Subjects").Annotation("Npgsql:IndexMethod", "gin");
            migrationBuilder.CreateIndex(name: "IX_audit_log_TargetId", table: "audit_log", column: "TargetId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(name: "IX_audit_log_EventType_CreatedAt", table: "audit_log");
            migrationBuilder.DropIndex(name: "IX_audit_log_Subjects", table: "audit_log");
            migrationBuilder.DropIndex(name: "IX_audit_log_TargetId", table: "audit_log");
            migrationBuilder.Sql("""
                UPDATE audit_log SET "Detail" = 'cli: ' || coalesce("Detail", '') WHERE "Actor" = 'Cli';
                UPDATE audit_log SET "Detail" = 'agent: ' || coalesce("Detail", '') WHERE "Actor" = 'Agent' AND "Username" = 'agent';
                """);
            foreach (var column in new[] { "Category", "Actor", "ActorRef", "Outcome", "Error", "TargetType", "TargetId", "TargetName", "Subjects",
                "Changes", "Request", "CorrelationId" })
            {
                migrationBuilder.DropColumn(name: column, table: "audit_log");
            }
            migrationBuilder.RenameIndex(name: "IX_audit_log_CreatedAt", table: "audit_log", newName: "IX_auth_log_CreatedAt");
            migrationBuilder.RenameIndex(name: "IX_audit_log_UserId_CreatedAt", table: "audit_log", newName: "IX_auth_log_UserId_CreatedAt");
            migrationBuilder.Sql("""ALTER TABLE audit_log RENAME CONSTRAINT "PK_audit_log" TO "PK_auth_log";""");
            migrationBuilder.RenameTable(name: "audit_log", newName: "auth_log");
        }
    }
}
