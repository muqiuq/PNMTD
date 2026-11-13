using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PNMTD.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddWindowsAgents : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "windows_agent_log",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SensorId = table.Column<Guid>(type: "uuid", nullable: true),
                    AgentName = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    At = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Kind = table.Column<int>(type: "integer", nullable: false),
                    Message = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    Details = table.Column<string>(type: "jsonb", nullable: true),
                    Address = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_windows_agent_log", x => x.Id);
                    table.ForeignKey(
                        name: "FK_windows_agent_log_sensors_SensorId",
                        column: x => x.SensorId,
                        principalTable: "sensors",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "windows_agents",
                columns: table => new
                {
                    SensorId = table.Column<Guid>(type: "uuid", nullable: false),
                    AgentId = table.Column<Guid>(type: "uuid", nullable: false),
                    PublicKey = table.Column<string>(type: "text", nullable: true),
                    KeyFingerprint = table.Column<string>(type: "text", nullable: false),
                    EncryptionKey = table.Column<string>(type: "text", nullable: true),
                    JoinedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    MachineGuid = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    Hostname = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    Fqdn = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    Domain = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    OsCaption = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    OsEdition = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    OsBuild = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    OsDisplayVersion = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    OsSupported = table.Column<bool>(type: "boolean", nullable: false),
                    Architecture = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    AgentVersion = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    Capabilities = table.Column<List<string>>(type: "text[]", nullable: false),
                    ConfigVersion = table.Column<int>(type: "integer", nullable: false),
                    Connected = table.Column<bool>(type: "boolean", nullable: false),
                    ConnectedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    DisconnectedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastSeenAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    RemoteAddress = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    BootTime = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ClockSkewSeconds = table.Column<int>(type: "integer", nullable: true),
                    Heartbeat = table.Column<string>(type: "jsonb", nullable: true),
                    ProblemHeartbeats = table.Column<int>(type: "integer", nullable: false),
                    LastProblemAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    AdminLoginEvents = table.Column<bool>(type: "boolean", nullable: false),
                    FailedLoginEvents = table.Column<bool>(type: "boolean", nullable: false),
                    FailedLoginThreshold = table.Column<int>(type: "integer", nullable: false),
                    CriticalEvents = table.Column<bool>(type: "boolean", nullable: false),
                    RevokedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    RevokedReason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    UninstalledAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_windows_agents", x => x.SensorId);
                    table.ForeignKey(
                        name: "FK_windows_agents_sensors_SensorId",
                        column: x => x.SensorId,
                        principalTable: "sensors",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "windows_join_tokens",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TokenHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Prefix = table.Column<string>(type: "character varying(2)", maxLength: 2, nullable: false),
                    HostId = table.Column<Guid>(type: "uuid", nullable: false),
                    SensorId = table.Column<Guid>(type: "uuid", nullable: true),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedByName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    ExpiresAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UsedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    UsedFromAddress = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    ResultSensorId = table.Column<Guid>(type: "uuid", nullable: true),
                    RevokedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_windows_join_tokens", x => x.Id);
                    table.ForeignKey(
                        name: "FK_windows_join_tokens_hosts_HostId",
                        column: x => x.HostId,
                        principalTable: "hosts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_windows_join_tokens_sensors_SensorId",
                        column: x => x.SensorId,
                        principalTable: "sensors",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "windows_settings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false),
                    SigningKeyProtected = table.Column<string>(type: "text", nullable: false),
                    SigningPublicKey = table.Column<string>(type: "text", nullable: false),
                    SigningKeyFingerprint = table.Column<string>(type: "text", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ConfigVersion = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_windows_settings", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_windows_agent_log_At",
                table: "windows_agent_log",
                column: "At");

            migrationBuilder.CreateIndex(
                name: "IX_windows_agent_log_SensorId_At",
                table: "windows_agent_log",
                columns: new[] { "SensorId", "At" });

            migrationBuilder.CreateIndex(
                name: "IX_windows_agents_AgentId",
                table: "windows_agents",
                column: "AgentId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_windows_agents_MachineGuid",
                table: "windows_agents",
                column: "MachineGuid");

            migrationBuilder.CreateIndex(
                name: "IX_windows_join_tokens_CreatedAt",
                table: "windows_join_tokens",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_windows_join_tokens_HostId",
                table: "windows_join_tokens",
                column: "HostId");

            migrationBuilder.CreateIndex(
                name: "IX_windows_join_tokens_SensorId",
                table: "windows_join_tokens",
                column: "SensorId");

            migrationBuilder.CreateIndex(
                name: "IX_windows_join_tokens_TokenHash",
                table: "windows_join_tokens",
                column: "TokenHash",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "windows_agent_log");

            migrationBuilder.DropTable(
                name: "windows_agents");

            migrationBuilder.DropTable(
                name: "windows_join_tokens");

            migrationBuilder.DropTable(
                name: "windows_settings");
        }
    }
}
