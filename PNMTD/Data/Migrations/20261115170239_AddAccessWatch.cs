using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PNMTD.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddAccessWatch : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<List<string>>(
                name: "ServerAccounts",
                table: "users",
                type: "text[]",
                nullable: false,
                defaultValueSql: "'{}'::text[]");

            migrationBuilder.AddColumn<string>(
                name: "ActiveUsers",
                table: "mikrotik_routers",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "LogGap",
                table: "mikrotik_routers",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTime>(
                name: "LogOldestAt",
                table: "mikrotik_routers",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "LogUnmatched",
                table: "mikrotik_routers",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTime>(
                name: "LoginsFirstReadAt",
                table: "mikrotik_routers",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PnmtAddress",
                table: "mikrotik_routers",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RouterUsers",
                table: "mikrotik_routers",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "WatchLogins",
                table: "mikrotik_routers",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "AccessAuditAt",
                table: "cookie_runtime",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "AccessDigestAt",
                table: "cookie_runtime",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "AccessMcpLogId",
                table: "cookie_runtime",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AccessAnswer",
                table: "cookie_cases",
                type: "character varying(16)",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "AccessAnsweredAt",
                table: "cookie_cases",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AccessAnsweredBy",
                table: "cookie_cases",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "AccessNotifiedAt",
                table: "cookie_cases",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AccessNotifiedVerdict",
                table: "cookie_cases",
                type: "character varying(16)",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AccessPendingReason",
                table: "cookie_cases",
                type: "character varying(300)",
                maxLength: 300,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AccessPendingSource",
                table: "cookie_cases",
                type: "character varying(16)",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AccessPendingVerdict",
                table: "cookie_cases",
                type: "character varying(16)",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AccessVerdict",
                table: "cookie_cases",
                type: "character varying(16)",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "SensorId",
                table: "cookie_cases",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "TicketAskedAt",
                table: "cookie_cases",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TicketAskedBy",
                table: "cookie_cases",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "access_known",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Scope = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Target = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    SensorId = table.Column<Guid>(type: "uuid", nullable: true),
                    LocationId = table.Column<Guid>(type: "uuid", nullable: true),
                    Account = table.Column<string>(type: "character varying(520)", maxLength: 520, nullable: true),
                    Source = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    Groups = table.Column<List<string>>(type: "text[]", nullable: false),
                    Until = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Comment = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    Label = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedById = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedByName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    FromCaseId = table.Column<Guid>(type: "uuid", nullable: true),
                    EndedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    EndedByName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_access_known", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "access_reviews",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Target = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    SensorId = table.Column<Guid>(type: "uuid", nullable: true),
                    TargetName = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    AgentLoginId = table.Column<Guid>(type: "uuid", nullable: true),
                    AuditLogId = table.Column<Guid>(type: "uuid", nullable: true),
                    McpLogId = table.Column<long>(type: "bigint", nullable: true),
                    At = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Account = table.Column<string>(type: "character varying(520)", maxLength: 520, nullable: false),
                    IsAdmin = table.Column<bool>(type: "boolean", nullable: false),
                    Way = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Group = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Source = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    Device = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    KeyFingerprint = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    TargetUser = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    Signals = table.Column<string>(type: "text", nullable: false),
                    Strong = table.Column<bool>(type: "boolean", nullable: false),
                    Late = table.Column<bool>(type: "boolean", nullable: false),
                    Flagged = table.Column<bool>(type: "boolean", nullable: false),
                    CaseId = table.Column<Guid>(type: "uuid", nullable: true),
                    State = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Confidence = table.Column<double>(type: "double precision", nullable: true),
                    Reason = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    JudgedBy = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    JudgedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    DecisionId = table.Column<long>(type: "bigint", nullable: true),
                    PersonId = table.Column<Guid>(type: "uuid", nullable: true),
                    AskedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    AskedAddresses = table.Column<List<string>>(type: "text[]", nullable: false),
                    TokenHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    TokenExpiresAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    PersonAnswer = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: true),
                    PersonAnsweredAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    PersonAnsweredFrom = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_access_reviews", x => x.Id);
                    table.ForeignKey(
                        name: "FK_access_reviews_agent_logins_AgentLoginId",
                        column: x => x.AgentLoginId,
                        principalTable: "agent_logins",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_access_reviews_cookie_cases_CaseId",
                        column: x => x.CaseId,
                        principalTable: "cookie_cases",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_access_reviews_sensors_SensorId",
                        column: x => x.SensorId,
                        principalTable: "sensors",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "IX_audit_log_EventType_Username_CreatedAt",
                table: "audit_log",
                columns: new[] { "EventType", "Username", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_agent_logins_SensorId_User_At",
                table: "agent_logins",
                columns: new[] { "SensorId", "User", "At" });

            migrationBuilder.CreateIndex(
                name: "IX_agent_logins_SourceAddress_At",
                table: "agent_logins",
                columns: new[] { "SourceAddress", "At" });

            migrationBuilder.CreateIndex(
                name: "IX_access_known_EndedAt",
                table: "access_known",
                column: "EndedAt");

            migrationBuilder.CreateIndex(
                name: "IX_access_reviews_AgentLoginId",
                table: "access_reviews",
                column: "AgentLoginId");

            migrationBuilder.CreateIndex(
                name: "IX_access_reviews_AuditLogId",
                table: "access_reviews",
                column: "AuditLogId");

            migrationBuilder.CreateIndex(
                name: "IX_access_reviews_CaseId",
                table: "access_reviews",
                column: "CaseId");

            migrationBuilder.CreateIndex(
                name: "IX_access_reviews_SensorId",
                table: "access_reviews",
                column: "SensorId");

            migrationBuilder.CreateIndex(
                name: "IX_access_reviews_Source_At",
                table: "access_reviews",
                columns: new[] { "Source", "At" });

            migrationBuilder.CreateIndex(
                name: "IX_access_reviews_State_CreatedAt",
                table: "access_reviews",
                columns: new[] { "State", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_access_reviews_TokenHash",
                table: "access_reviews",
                column: "TokenHash",
                unique: true,
                filter: "\"TokenHash\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "access_known");

            migrationBuilder.DropTable(
                name: "access_reviews");

            migrationBuilder.DropIndex(
                name: "IX_audit_log_EventType_Username_CreatedAt",
                table: "audit_log");

            migrationBuilder.DropIndex(
                name: "IX_agent_logins_SensorId_User_At",
                table: "agent_logins");

            migrationBuilder.DropIndex(
                name: "IX_agent_logins_SourceAddress_At",
                table: "agent_logins");

            migrationBuilder.DropColumn(
                name: "ServerAccounts",
                table: "users");

            migrationBuilder.DropColumn(
                name: "ActiveUsers",
                table: "mikrotik_routers");

            migrationBuilder.DropColumn(
                name: "LogGap",
                table: "mikrotik_routers");

            migrationBuilder.DropColumn(
                name: "LogOldestAt",
                table: "mikrotik_routers");

            migrationBuilder.DropColumn(
                name: "LogUnmatched",
                table: "mikrotik_routers");

            migrationBuilder.DropColumn(
                name: "LoginsFirstReadAt",
                table: "mikrotik_routers");

            migrationBuilder.DropColumn(
                name: "PnmtAddress",
                table: "mikrotik_routers");

            migrationBuilder.DropColumn(
                name: "RouterUsers",
                table: "mikrotik_routers");

            migrationBuilder.DropColumn(
                name: "WatchLogins",
                table: "mikrotik_routers");

            migrationBuilder.DropColumn(
                name: "AccessAuditAt",
                table: "cookie_runtime");

            migrationBuilder.DropColumn(
                name: "AccessDigestAt",
                table: "cookie_runtime");

            migrationBuilder.DropColumn(
                name: "AccessMcpLogId",
                table: "cookie_runtime");

            migrationBuilder.DropColumn(
                name: "AccessAnswer",
                table: "cookie_cases");

            migrationBuilder.DropColumn(
                name: "AccessAnsweredAt",
                table: "cookie_cases");

            migrationBuilder.DropColumn(
                name: "AccessAnsweredBy",
                table: "cookie_cases");

            migrationBuilder.DropColumn(
                name: "AccessNotifiedAt",
                table: "cookie_cases");

            migrationBuilder.DropColumn(
                name: "AccessNotifiedVerdict",
                table: "cookie_cases");

            migrationBuilder.DropColumn(
                name: "AccessPendingReason",
                table: "cookie_cases");

            migrationBuilder.DropColumn(
                name: "AccessPendingSource",
                table: "cookie_cases");

            migrationBuilder.DropColumn(
                name: "AccessPendingVerdict",
                table: "cookie_cases");

            migrationBuilder.DropColumn(
                name: "AccessVerdict",
                table: "cookie_cases");

            migrationBuilder.DropColumn(
                name: "SensorId",
                table: "cookie_cases");

            migrationBuilder.DropColumn(
                name: "TicketAskedAt",
                table: "cookie_cases");

            migrationBuilder.DropColumn(
                name: "TicketAskedBy",
                table: "cookie_cases");
        }
    }
}
