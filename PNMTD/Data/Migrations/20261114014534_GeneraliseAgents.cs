using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PNMTD.Data.Migrations
{
    /// <summary>
    /// L1 of docs/linux-sensor-plan.md (§3.3, §9.3): the Windows agent infrastructure becomes every platform's. Renames, so a
    /// development database keeps its servers, logins and scripts: windows_* → agent_* (windows_agents → agents), with their
    /// keys, foreign keys and indexes; agents.MachineGuid → MachineId. Adds the platform to agents and agent_join_tokens and
    /// the language to agent_scripts (existing rows: Windows, PowerShell), and renames the shared audit events Windows* →
    /// Agent*. windows_run_as stays.
    /// </summary>
    public partial class GeneraliseAgents : Migration
    {
        private static readonly (string Old, string New)[] Tables =
        [
            ("windows_settings", "agent_settings"),
            ("windows_join_tokens", "agent_join_tokens"),
            ("windows_agents", "agents"),
            ("windows_sections", "agent_sections"),
            ("windows_logins", "agent_logins"),
            ("windows_changes", "agent_changes"),
            ("windows_agent_log", "agent_log"),
            ("windows_scripts", "agent_scripts"),
            ("windows_script_versions", "agent_script_versions"),
            ("windows_script_runs", "agent_script_runs"),
        ];

        /// <summary>Primary and foreign keys, by their table's new name: PostgreSQL renames a key's index with it.</summary>
        private static readonly (string Table, string Old, string New)[] Keys =
        [
            ("agent_settings", "PK_windows_settings", "PK_agent_settings"),
            ("agent_join_tokens", "PK_windows_join_tokens", "PK_agent_join_tokens"),
            ("agent_join_tokens", "FK_windows_join_tokens_hosts_HostId", "FK_agent_join_tokens_hosts_HostId"),
            ("agent_join_tokens", "FK_windows_join_tokens_sensors_SensorId", "FK_agent_join_tokens_sensors_SensorId"),
            ("agents", "PK_windows_agents", "PK_agents"),
            ("agents", "FK_windows_agents_sensors_SensorId", "FK_agents_sensors_SensorId"),
            ("agent_sections", "PK_windows_sections", "PK_agent_sections"),
            ("agent_sections", "FK_windows_sections_sensors_SensorId", "FK_agent_sections_sensors_SensorId"),
            ("agent_logins", "PK_windows_logins", "PK_agent_logins"),
            ("agent_logins", "FK_windows_logins_sensors_SensorId", "FK_agent_logins_sensors_SensorId"),
            ("agent_changes", "PK_windows_changes", "PK_agent_changes"),
            ("agent_changes", "FK_windows_changes_sensors_SensorId", "FK_agent_changes_sensors_SensorId"),
            ("agent_log", "PK_windows_agent_log", "PK_agent_log"),
            ("agent_log", "FK_windows_agent_log_sensors_SensorId", "FK_agent_log_sensors_SensorId"),
            ("agent_scripts", "PK_windows_scripts", "PK_agent_scripts"),
            ("agent_script_versions", "PK_windows_script_versions", "PK_agent_script_versions"),
            ("agent_script_versions", "FK_windows_script_versions_windows_scripts_ScriptId", "FK_agent_script_versions_agent_scripts_ScriptId"),
            ("agent_script_runs", "PK_windows_script_runs", "PK_agent_script_runs"),
            ("agent_script_runs", "FK_windows_script_runs_sensors_SensorId", "FK_agent_script_runs_sensors_SensorId"),
            ("agent_script_runs", "FK_windows_script_runs_windows_scripts_ScriptId", "FK_agent_script_runs_agent_scripts_ScriptId"),
        ];

        private static readonly (string Table, string Old, string New)[] Indexes =
        [
            ("agent_join_tokens", "IX_windows_join_tokens_CreatedAt", "IX_agent_join_tokens_CreatedAt"),
            ("agent_join_tokens", "IX_windows_join_tokens_HostId", "IX_agent_join_tokens_HostId"),
            ("agent_join_tokens", "IX_windows_join_tokens_SensorId", "IX_agent_join_tokens_SensorId"),
            ("agent_join_tokens", "IX_windows_join_tokens_TokenHash", "IX_agent_join_tokens_TokenHash"),
            ("agents", "IX_windows_agents_AgentId", "IX_agents_AgentId"),
            ("agents", "IX_windows_agents_MachineGuid", "IX_agents_MachineId"),
            ("agent_logins", "IX_windows_logins_At", "IX_agent_logins_At"),
            ("agent_logins", "IX_windows_logins_SensorId_At", "IX_agent_logins_SensorId_At"),
            ("agent_logins", "IX_windows_logins_SensorId_Channel_RecordId", "IX_agent_logins_SensorId_Channel_RecordId"),
            ("agent_changes", "IX_windows_changes_At", "IX_agent_changes_At"),
            ("agent_changes", "IX_windows_changes_SensorId_At", "IX_agent_changes_SensorId_At"),
            ("agent_log", "IX_windows_agent_log_At", "IX_agent_log_At"),
            ("agent_log", "IX_windows_agent_log_SensorId_At", "IX_agent_log_SensorId_At"),
            ("agent_scripts", "IX_windows_scripts_Name", "IX_agent_scripts_Name"),
            ("agent_script_versions", "IX_windows_script_versions_ParseState_Hash", "IX_agent_script_versions_ParseState_Hash"),
            ("agent_script_versions", "IX_windows_script_versions_ScriptId_Number", "IX_agent_script_versions_ScriptId_Number"),
            ("agent_script_runs", "IX_windows_script_runs_BatchId", "IX_agent_script_runs_BatchId"),
            ("agent_script_runs", "IX_windows_script_runs_ScriptId", "IX_agent_script_runs_ScriptId"),
            ("agent_script_runs", "IX_windows_script_runs_SensorId_StartedAt", "IX_agent_script_runs_SensorId_StartedAt"),
            ("agent_script_runs", "IX_windows_script_runs_StartedAt", "IX_agent_script_runs_StartedAt"),
        ];

        private static readonly (string Old, string New)[] AuditEvents =
        [
            ("WindowsJoinTokenCreated", "AgentJoinTokenCreated"),
            ("WindowsJoinTokenRevoked", "AgentJoinTokenRevoked"),
            ("WindowsJoined", "AgentJoined"),
            ("WindowsJoinFailed", "AgentJoinFailed"),
            ("WindowsAgentRevoked", "AgentRevoked"),
            ("WindowsScriptChanged", "AgentScriptChanged"),
            ("WindowsScriptRun", "AgentScriptRun"),
        ];

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            foreach (var (old, @new) in Tables)
            {
                migrationBuilder.RenameTable(name: old, newName: @new);
            }
            foreach (var (table, old, @new) in Keys)
            {
                migrationBuilder.Sql($"ALTER TABLE {table} RENAME CONSTRAINT \"{old}\" TO \"{@new}\";");
            }
            foreach (var (old, @new) in Tables)
            {
                migrationBuilder.Sql(RenameNotNullConstraints(@new, old, @new));
            }
            migrationBuilder.RenameColumn(name: "MachineGuid", table: "agents", newName: "MachineId");
            foreach (var (table, old, @new) in Indexes)
            {
                migrationBuilder.RenameIndex(name: old, table: table, newName: @new);
            }

            migrationBuilder.AddColumn<int>(
                name: "Platform",
                table: "agents",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "Platform",
                table: "agent_join_tokens",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "Language",
                table: "agent_scripts",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            foreach (var (old, @new) in AuditEvents)
            {
                migrationBuilder.Sql($"UPDATE auth_log SET \"EventType\" = '{@new}' WHERE \"EventType\" = '{old}';");
            }
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            foreach (var (old, @new) in AuditEvents)
            {
                migrationBuilder.Sql($"UPDATE auth_log SET \"EventType\" = '{old}' WHERE \"EventType\" = '{@new}';");
            }

            migrationBuilder.DropColumn(name: "Language", table: "agent_scripts");
            migrationBuilder.DropColumn(name: "Platform", table: "agent_join_tokens");
            migrationBuilder.DropColumn(name: "Platform", table: "agents");

            foreach (var (table, old, @new) in Indexes)
            {
                migrationBuilder.RenameIndex(name: @new, table: table, newName: old);
            }
            migrationBuilder.RenameColumn(name: "MachineId", table: "agents", newName: "MachineGuid");
            foreach (var (old, @new) in Tables)
            {
                migrationBuilder.Sql(RenameNotNullConstraints(@new, @new, old));
            }
            foreach (var (table, old, @new) in Keys)
            {
                migrationBuilder.Sql($"ALTER TABLE {table} RENAME CONSTRAINT \"{@new}\" TO \"{old}\";");
            }
            foreach (var (old, @new) in Tables)
            {
                migrationBuilder.RenameTable(name: @new, newName: old);
            }
        }

        /// <summary>
        /// PostgreSQL 18 names a column's NOT NULL constraint after its table ("windows_agents_Hostname_not_null"), and a table's
        /// rename keeps it: these follow the table's name. Older servers have none, and the loop finds nothing.
        /// </summary>
        private static string RenameNotNullConstraints(string table, string from, string to) => $$"""
            DO $$
            DECLARE r record;
            BEGIN
                FOR r IN SELECT c.conname FROM pg_constraint c JOIN pg_class t ON t.oid = c.conrelid
                         WHERE c.contype = 'n' AND t.relname = '{{table}}' AND starts_with(c.conname, '{{from}}_') LOOP
                    EXECUTE format('ALTER TABLE %I RENAME CONSTRAINT %I TO %I', '{{table}}', r.conname, '{{to}}' || substr(r.conname, {{from.Length + 1}}));
                END LOOP;
            END $$;
            """;
    }
}
