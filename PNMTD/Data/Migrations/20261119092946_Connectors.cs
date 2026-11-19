using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PNMTD.Data.Migrations
{
    /// <inheritdoc />
    public partial class Connectors : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "ConnectorId",
                table: "agent_script_runs",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Function",
                table: "agent_script_runs",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "connector_callers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Description = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false),
                    Networks = table.Column<string>(type: "jsonb", nullable: false),
                    KeyHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    KeyProtected = table.Column<string>(type: "text", nullable: false),
                    KeyHint = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    KeyIssuedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedBy = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LastCallAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastCallFrom = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_connector_callers", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "connectors",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Slug = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Purpose = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    Type = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false),
                    Mode = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    WaitSeconds = table.Column<int>(type: "integer", nullable: false),
                    ReturnOutput = table.Column<bool>(type: "boolean", nullable: false),
                    MaxCallsPerHour = table.Column<int>(type: "integer", nullable: false),
                    MaxQueued = table.Column<int>(type: "integer", nullable: false),
                    MaxQueueSeconds = table.Column<int>(type: "integer", nullable: false),
                    ReviewEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    ReviewPurpose = table.Column<bool>(type: "boolean", nullable: false),
                    ReviewThreshold = table.Column<double>(type: "double precision", nullable: true),
                    ReviewChecks = table.Column<string>(type: "jsonb", nullable: false),
                    ReviewFallback = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedBy = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LastCallAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_connectors", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "connector_arguments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ConnectorId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Mode = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Required = table.Column<bool>(type: "boolean", nullable: false),
                    Pattern = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    FixedValue = table.Column<string>(type: "jsonb", nullable: true),
                    Description = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    Multiline = table.Column<bool>(type: "boolean", nullable: false),
                    Order = table.Column<int>(type: "integer", nullable: false),
                    Parameter = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_connector_arguments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_connector_arguments_connectors_ConnectorId",
                        column: x => x.ConnectorId,
                        principalTable: "connectors",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "connector_calls",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ConnectorId = table.Column<Guid>(type: "uuid", nullable: true),
                    ConnectorName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Slug = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    CallerId = table.Column<Guid>(type: "uuid", nullable: true),
                    CallerName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    ReceivedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    From = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    State = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Accepted = table.Column<bool>(type: "boolean", nullable: false),
                    DryRun = table.Column<bool>(type: "boolean", nullable: false),
                    Mode = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    RefusedBy = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    WaitingFor = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    ExpiresAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    StartedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    FinishedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ServerSensorId = table.Column<Guid>(type: "uuid", nullable: true),
                    ServerName = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    ScriptName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    ScriptRevision = table.Column<int>(type: "integer", nullable: true),
                    Hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    Function = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    Message = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    Arguments = table.Column<string>(type: "jsonb", nullable: true),
                    SecureNames = table.Column<List<string>>(type: "text[]", nullable: false),
                    Reference = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    Note = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    Redacted = table.Column<bool>(type: "boolean", nullable: false),
                    IdempotencyKey = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    ArgumentsHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    Review = table.Column<string>(type: "jsonb", nullable: true),
                    RunId = table.Column<Guid>(type: "uuid", nullable: true),
                    CollectedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_connector_calls", x => x.Id);
                    table.ForeignKey(
                        name: "FK_connector_calls_agent_script_runs_RunId",
                        column: x => x.RunId,
                        principalTable: "agent_script_runs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_connector_calls_connector_callers_CallerId",
                        column: x => x.CallerId,
                        principalTable: "connector_callers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_connector_calls_connectors_ConnectorId",
                        column: x => x.ConnectorId,
                        principalTable: "connectors",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "connector_grants",
                columns: table => new
                {
                    CallerId = table.Column<Guid>(type: "uuid", nullable: false),
                    ConnectorId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(420)", maxLength: 420, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedBy = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_connector_grants", x => new { x.CallerId, x.ConnectorId });
                    table.ForeignKey(
                        name: "FK_connector_grants_connector_callers_CallerId",
                        column: x => x.CallerId,
                        principalTable: "connector_callers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_connector_grants_connectors_ConnectorId",
                        column: x => x.ConnectorId,
                        principalTable: "connectors",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "connector_scripts",
                columns: table => new
                {
                    ConnectorId = table.Column<Guid>(type: "uuid", nullable: false),
                    Server = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    ScriptText = table.Column<string>(type: "text", nullable: false),
                    Hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ScriptRevision = table.Column<int>(type: "integer", nullable: false),
                    TakenFrom = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    ParseState = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Parsed = table.Column<string>(type: "jsonb", nullable: true),
                    ParseError = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    ParsedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    NeedsReview = table.Column<bool>(type: "boolean", nullable: false),
                    Function = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    RunAsKind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Account = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    Shell = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    TimeoutSeconds = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_connector_scripts", x => x.ConnectorId);
                    table.ForeignKey(
                        name: "FK_connector_scripts_connectors_ConnectorId",
                        column: x => x.ConnectorId,
                        principalTable: "connectors",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_agent_script_runs_ConnectorId",
                table: "agent_script_runs",
                column: "ConnectorId");

            migrationBuilder.CreateIndex(
                name: "IX_agent_script_runs_unfinished",
                table: "agent_script_runs",
                columns: new[] { "SensorId", "ScriptName" },
                filter: "\"State\" IN (0, 1)");

            migrationBuilder.CreateIndex(
                name: "IX_connector_arguments_ConnectorId_Name",
                table: "connector_arguments",
                columns: new[] { "ConnectorId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_connector_callers_KeyHash",
                table: "connector_callers",
                column: "KeyHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_connector_callers_Name",
                table: "connector_callers",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_connector_calls_active",
                table: "connector_calls",
                column: "ReceivedAt",
                filter: "\"State\" IN ('Queued', 'Running')");

            migrationBuilder.CreateIndex(
                name: "IX_connector_calls_CallerId_ConnectorId_IdempotencyKey",
                table: "connector_calls",
                columns: new[] { "CallerId", "ConnectorId", "IdempotencyKey" },
                unique: true,
                filter: "\"IdempotencyKey\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_connector_calls_CallerId_ReceivedAt",
                table: "connector_calls",
                columns: new[] { "CallerId", "ReceivedAt" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "IX_connector_calls_ConnectorId_ReceivedAt",
                table: "connector_calls",
                columns: new[] { "ConnectorId", "ReceivedAt" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "IX_connector_calls_ReceivedAt",
                table: "connector_calls",
                column: "ReceivedAt");

            migrationBuilder.CreateIndex(
                name: "IX_connector_calls_RunId",
                table: "connector_calls",
                column: "RunId");

            migrationBuilder.CreateIndex(
                name: "IX_connector_grants_ConnectorId",
                table: "connector_grants",
                column: "ConnectorId");

            migrationBuilder.CreateIndex(
                name: "IX_connector_scripts_Hash",
                table: "connector_scripts",
                column: "Hash");

            migrationBuilder.CreateIndex(
                name: "IX_connectors_Name",
                table: "connectors",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_connectors_Slug",
                table: "connectors",
                column: "Slug",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "connector_arguments");

            migrationBuilder.DropTable(
                name: "connector_calls");

            migrationBuilder.DropTable(
                name: "connector_grants");

            migrationBuilder.DropTable(
                name: "connector_scripts");

            migrationBuilder.DropTable(
                name: "connector_callers");

            migrationBuilder.DropTable(
                name: "connectors");

            migrationBuilder.DropIndex(
                name: "IX_agent_script_runs_ConnectorId",
                table: "agent_script_runs");

            migrationBuilder.DropIndex(
                name: "IX_agent_script_runs_unfinished",
                table: "agent_script_runs");

            migrationBuilder.DropColumn(
                name: "ConnectorId",
                table: "agent_script_runs");

            migrationBuilder.DropColumn(
                name: "Function",
                table: "agent_script_runs");
        }
    }
}
