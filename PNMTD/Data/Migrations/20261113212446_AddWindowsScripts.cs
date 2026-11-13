using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PNMTD.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddWindowsScripts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "windows_run_as",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Kind = table.Column<int>(type: "integer", nullable: false),
                    Account = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    Description = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_windows_run_as", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "windows_scripts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Description = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CurrentVersion = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_windows_scripts", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "windows_script_runs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    BatchId = table.Column<Guid>(type: "uuid", nullable: false),
                    ScriptId = table.Column<Guid>(type: "uuid", nullable: true),
                    ScriptName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    VersionNumber = table.Column<int>(type: "integer", nullable: false),
                    Hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    SensorId = table.Column<Guid>(type: "uuid", nullable: true),
                    ServerName = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    RunAsName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    RunAsKind = table.Column<int>(type: "integer", nullable: false),
                    Account = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    Arguments = table.Column<string>(type: "jsonb", nullable: true),
                    Redacted = table.Column<bool>(type: "boolean", nullable: false),
                    RequestedBy = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    StartedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    FinishedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    State = table.Column<int>(type: "integer", nullable: false),
                    ExitCode = table.Column<int>(type: "integer", nullable: true),
                    Output = table.Column<string>(type: "text", nullable: true),
                    Message = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    DurationMs = table.Column<double>(type: "double precision", nullable: true),
                    TimeoutSeconds = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_windows_script_runs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_windows_script_runs_sensors_SensorId",
                        column: x => x.SensorId,
                        principalTable: "sensors",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_windows_script_runs_windows_scripts_ScriptId",
                        column: x => x.ScriptId,
                        principalTable: "windows_scripts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "windows_script_versions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ScriptId = table.Column<Guid>(type: "uuid", nullable: false),
                    Number = table.Column<int>(type: "integer", nullable: false),
                    Text = table.Column<string>(type: "text", nullable: false),
                    Hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedByName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    ParseState = table.Column<int>(type: "integer", nullable: false),
                    Parsed = table.Column<string>(type: "jsonb", nullable: true),
                    ParseError = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    ParsedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ParsedOn = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_windows_script_versions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_windows_script_versions_windows_scripts_ScriptId",
                        column: x => x.ScriptId,
                        principalTable: "windows_scripts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_windows_run_as_Name",
                table: "windows_run_as",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_windows_script_runs_BatchId",
                table: "windows_script_runs",
                column: "BatchId");

            migrationBuilder.CreateIndex(
                name: "IX_windows_script_runs_ScriptId",
                table: "windows_script_runs",
                column: "ScriptId");

            migrationBuilder.CreateIndex(
                name: "IX_windows_script_runs_SensorId_StartedAt",
                table: "windows_script_runs",
                columns: new[] { "SensorId", "StartedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_windows_script_runs_StartedAt",
                table: "windows_script_runs",
                column: "StartedAt");

            migrationBuilder.CreateIndex(
                name: "IX_windows_script_versions_ParseState_Hash",
                table: "windows_script_versions",
                columns: new[] { "ParseState", "Hash" });

            migrationBuilder.CreateIndex(
                name: "IX_windows_script_versions_ScriptId_Number",
                table: "windows_script_versions",
                columns: new[] { "ScriptId", "Number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_windows_scripts_Name",
                table: "windows_scripts",
                column: "Name",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "windows_run_as");

            migrationBuilder.DropTable(
                name: "windows_script_runs");

            migrationBuilder.DropTable(
                name: "windows_script_versions");

            migrationBuilder.DropTable(
                name: "windows_scripts");
        }
    }
}
