using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PNMTD.Data.Migrations
{
    /// <inheritdoc />
    /// <summary>
    /// docs/linux-os-updates-plan.md §5.2: the runs of apt and their logs, the pending packages, the findings with their acknowledgements,
    /// the advisory sources and what they hold (fixes, CVE risks, changelogs, advisory texts); on the agent row the server's switches (OS
    /// updates, high-risk alerts and update failures on, as for a new server, existing rows included) and what the heartbeat reads.
    /// </summary>
    public partial class AddOsUpdates : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "DpkgInterrupted",
                table: "agents",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "HighRiskEvents",
                table: "agents",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<string>(
                name: "HighRiskMessage",
                table: "agents",
                type: "character varying(2000)",
                maxLength: 2000,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "LastUpgradeAt",
                table: "agents",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ListsUpdatedAt",
                table: "agents",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "OsDistribution",
                table: "agents",
                type: "character varying(60)",
                maxLength: 60,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "OsUpdatesEnabled",
                table: "agents",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<int>(
                name: "PendingSecurity",
                table: "agents",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "PendingUpdates",
                table: "agents",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<bool>(
                name: "UpdateFailedEvents",
                table: "agents",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<string>(
                name: "UpdateFailedMessage",
                table: "agents",
                type: "character varying(2000)",
                maxLength: 2000,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "os_advisory_sources",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<int>(type: "integer", nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Url = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false),
                    IntervalMinutes = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LastFetchAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastSuccessAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ETag = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    LastModified = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    Entries = table.Column<int>(type: "integer", nullable: false),
                    Error = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    Cursor = table.Column<string>(type: "jsonb", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_os_advisory_sources", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "os_cve_risks",
                columns: table => new
                {
                    Cve = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Kev = table.Column<bool>(type: "boolean", nullable: false),
                    KevAddedAt = table.Column<DateOnly>(type: "date", nullable: true),
                    KevRansomware = table.Column<bool>(type: "boolean", nullable: false),
                    KevName = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    Epss = table.Column<double>(type: "double precision", nullable: true),
                    EpssPercentile = table.Column<double>(type: "double precision", nullable: true),
                    EpssDate = table.Column<DateOnly>(type: "date", nullable: true),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_os_cve_risks", x => x.Cve);
                });

            migrationBuilder.CreateTable(
                name: "os_package_changelogs",
                columns: table => new
                {
                    Distribution = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    SourcePackage = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Version = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    InstalledVersion = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    Cves = table.Column<List<string>>(type: "text[]", nullable: false),
                    Urgencies = table.Column<List<string>>(type: "text[]", nullable: false),
                    Text = table.Column<string>(type: "text", nullable: false),
                    FetchedFrom = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    ReceivedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_os_package_changelogs", x => new { x.Distribution, x.SourcePackage, x.Version });
                });

            migrationBuilder.CreateTable(
                name: "os_pending_packages",
                columns: table => new
                {
                    SensorId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Arch = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Installed = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    Candidate = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Source = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    SourceVersion = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    InstalledSourceVersion = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    Origin = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    Security = table.Column<bool>(type: "boolean", nullable: false),
                    InUpgrade = table.Column<bool>(type: "boolean", nullable: false),
                    Phased = table.Column<bool>(type: "boolean", nullable: false),
                    FirstSeenAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_os_pending_packages", x => new { x.SensorId, x.Name, x.Arch });
                    table.ForeignKey(
                        name: "FK_os_pending_packages_sensors_SensorId",
                        column: x => x.SensorId,
                        principalTable: "sensors",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "os_update_findings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SensorId = table.Column<Guid>(type: "uuid", nullable: false),
                    SourcePackage = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Cve = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Packages = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    Installed = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    Candidate = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Rating = table.Column<int>(type: "integer", nullable: false),
                    Reasons = table.Column<string>(type: "jsonb", nullable: false),
                    FirstSeenAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ResolvedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    AcknowledgedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    AcknowledgedById = table.Column<Guid>(type: "uuid", nullable: true),
                    AcknowledgedByName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    AcknowledgedReason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    AcknowledgedUntil = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    AcknowledgedKinds = table.Column<List<string>>(type: "text[]", nullable: false),
                    AcknowledgementEndedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    AcknowledgementEndedReason = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_os_update_findings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_os_update_findings_sensors_SensorId",
                        column: x => x.SensorId,
                        principalTable: "sensors",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "os_update_runs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SensorId = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<int>(type: "integer", nullable: false),
                    Mode = table.Column<int>(type: "integer", nullable: true),
                    Trigger = table.Column<int>(type: "integer", nullable: false),
                    RequestedById = table.Column<Guid>(type: "uuid", nullable: true),
                    RequestedByName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    RequestedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    StartedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    FinishedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    State = table.Column<int>(type: "integer", nullable: false),
                    ExitCode = table.Column<int>(type: "integer", nullable: true),
                    Summary = table.Column<string>(type: "jsonb", nullable: true),
                    Message = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    Command = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    LogBytes = table.Column<long>(type: "bigint", nullable: false),
                    ReportedLogBytes = table.Column<long>(type: "bigint", nullable: true),
                    LogSha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    LogTruncated = table.Column<bool>(type: "boolean", nullable: false),
                    LogIncomplete = table.Column<bool>(type: "boolean", nullable: false),
                    LastHeardAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ExternalId = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    DismissedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    DismissedByName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_os_update_runs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_os_update_runs_sensors_SensorId",
                        column: x => x.SensorId,
                        principalTable: "sensors",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "os_advisory_items",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SourceId = table.Column<Guid>(type: "uuid", nullable: false),
                    Guid = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    Hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Title = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    Link = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    PublishedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    FirstSeenAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Text = table.Column<string>(type: "text", nullable: false),
                    Cves = table.Column<List<string>>(type: "text[]", nullable: false),
                    Keywords = table.Column<List<string>>(type: "text[]", nullable: false),
                    AssessedBy = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Assessment = table.Column<string>(type: "jsonb", nullable: true),
                    AssessedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    AiWorkId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_os_advisory_items", x => x.Id);
                    table.ForeignKey(
                        name: "FK_os_advisory_items_os_advisory_sources_SourceId",
                        column: x => x.SourceId,
                        principalTable: "os_advisory_sources",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "os_fixes",
                columns: table => new
                {
                    SourceId = table.Column<Guid>(type: "uuid", nullable: false),
                    Distribution = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    SourcePackage = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Cve = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    FixedVersion = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Urgency = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: true),
                    Scope = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_os_fixes", x => new { x.SourceId, x.Distribution, x.SourcePackage, x.Cve });
                    table.ForeignKey(
                        name: "FK_os_fixes_os_advisory_sources_SourceId",
                        column: x => x.SourceId,
                        principalTable: "os_advisory_sources",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "os_update_log_chunks",
                columns: table => new
                {
                    RunId = table.Column<Guid>(type: "uuid", nullable: false),
                    Offset = table.Column<long>(type: "bigint", nullable: false),
                    Text = table.Column<string>(type: "text", nullable: false),
                    Bytes = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_os_update_log_chunks", x => new { x.RunId, x.Offset });
                    table.ForeignKey(
                        name: "FK_os_update_log_chunks_os_update_runs_RunId",
                        column: x => x.RunId,
                        principalTable: "os_update_runs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_os_advisory_items_FirstSeenAt",
                table: "os_advisory_items",
                column: "FirstSeenAt");

            migrationBuilder.CreateIndex(
                name: "IX_os_advisory_items_SourceId_Guid_Hash",
                table: "os_advisory_items",
                columns: new[] { "SourceId", "Guid", "Hash" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_os_advisory_sources_Kind",
                table: "os_advisory_sources",
                column: "Kind");

            migrationBuilder.CreateIndex(
                name: "IX_os_fixes_Distribution_SourcePackage",
                table: "os_fixes",
                columns: new[] { "Distribution", "SourcePackage" });

            migrationBuilder.CreateIndex(
                name: "IX_os_package_changelogs_ReceivedAt",
                table: "os_package_changelogs",
                column: "ReceivedAt");

            migrationBuilder.CreateIndex(
                name: "IX_os_pending_packages_Source_SourceVersion",
                table: "os_pending_packages",
                columns: new[] { "Source", "SourceVersion" });

            migrationBuilder.CreateIndex(
                name: "IX_os_update_findings_open",
                table: "os_update_findings",
                columns: new[] { "SensorId", "SourcePackage", "Cve" },
                unique: true,
                filter: "\"ResolvedAt\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_os_update_findings_ResolvedAt",
                table: "os_update_findings",
                column: "ResolvedAt");

            migrationBuilder.CreateIndex(
                name: "IX_os_update_runs_SensorId_ExternalId",
                table: "os_update_runs",
                columns: new[] { "SensorId", "ExternalId" },
                unique: true,
                filter: "\"ExternalId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_os_update_runs_SensorId_Kind_RequestedAt",
                table: "os_update_runs",
                columns: new[] { "SensorId", "Kind", "RequestedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_os_update_runs_State",
                table: "os_update_runs",
                column: "State");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "os_advisory_items");

            migrationBuilder.DropTable(
                name: "os_cve_risks");

            migrationBuilder.DropTable(
                name: "os_fixes");

            migrationBuilder.DropTable(
                name: "os_package_changelogs");

            migrationBuilder.DropTable(
                name: "os_pending_packages");

            migrationBuilder.DropTable(
                name: "os_update_findings");

            migrationBuilder.DropTable(
                name: "os_update_log_chunks");

            migrationBuilder.DropTable(
                name: "os_advisory_sources");

            migrationBuilder.DropTable(
                name: "os_update_runs");

            migrationBuilder.DropColumn(
                name: "DpkgInterrupted",
                table: "agents");

            migrationBuilder.DropColumn(
                name: "HighRiskEvents",
                table: "agents");

            migrationBuilder.DropColumn(
                name: "HighRiskMessage",
                table: "agents");

            migrationBuilder.DropColumn(
                name: "LastUpgradeAt",
                table: "agents");

            migrationBuilder.DropColumn(
                name: "ListsUpdatedAt",
                table: "agents");

            migrationBuilder.DropColumn(
                name: "OsDistribution",
                table: "agents");

            migrationBuilder.DropColumn(
                name: "OsUpdatesEnabled",
                table: "agents");

            migrationBuilder.DropColumn(
                name: "PendingSecurity",
                table: "agents");

            migrationBuilder.DropColumn(
                name: "PendingUpdates",
                table: "agents");

            migrationBuilder.DropColumn(
                name: "UpdateFailedEvents",
                table: "agents");

            migrationBuilder.DropColumn(
                name: "UpdateFailedMessage",
                table: "agents");
        }
    }
}
