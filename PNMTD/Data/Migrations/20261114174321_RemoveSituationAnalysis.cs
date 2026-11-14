using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PNMTD.Data.Migrations
{
    /// <inheritdoc />
    public partial class RemoveSituationAnalysis : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // The situation analysis goes (docs/cookie-plan.md §12): Cookie's status replaces it. Its decisions, and its three
            // system sensors with their events and notification links, go with it, so that no red sensor is left behind.
            migrationBuilder.Sql("""DELETE FROM decisions WHERE "Kind" = 'analysis.finding'""");
            migrationBuilder.Sql("""UPDATE ai_work SET "State" = 6 WHERE "Purpose" = 'situation.analyse' AND "State" IN (0, 1, 2, 3)""");
            migrationBuilder.Sql("""
                UPDATE mailinputrules SET "SensorOutputId" = NULL WHERE "SensorOutputId" IN
                    (SELECT "Id" FROM sensors WHERE "Type" = 90 AND "Parameters" IN ('ai-analysis-critical', 'ai-analysis-warning', 'ai-analysis-info'));
                UPDATE sensors SET "OlderSiblingId" = NULL WHERE "OlderSiblingId" IN
                    (SELECT "Id" FROM sensors WHERE "Type" = 90 AND "Parameters" IN ('ai-analysis-critical', 'ai-analysis-warning', 'ai-analysis-info'));
                DELETE FROM sensors WHERE "Type" = 90 AND "Parameters" IN ('ai-analysis-critical', 'ai-analysis-warning', 'ai-analysis-info');
                """);
            migrationBuilder.DropTable(
                name: "ai_findings");

            migrationBuilder.DropTable(
                name: "ai_analysis_runs");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ai_analysis_runs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    At = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CompletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Error = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    GuardFlagged = table.Column<bool>(type: "boolean", nullable: false),
                    Mapping = table.Column<string>(type: "jsonb", nullable: false),
                    SnapshotChars = table.Column<int>(type: "integer", nullable: false),
                    SnapshotHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    State = table.Column<short>(type: "smallint", nullable: false),
                    Status = table.Column<short>(type: "smallint", nullable: true),
                    Summary = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    Trigger = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Withheld = table.Column<int>(type: "integer", nullable: false),
                    WorkId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ai_analysis_runs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ai_analysis_runs_ai_work_WorkId",
                        column: x => x.WorkId,
                        principalTable: "ai_work",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "ai_findings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    RunId = table.Column<Guid>(type: "uuid", nullable: false),
                    Confidence = table.Column<double>(type: "double precision", nullable: false),
                    DecisionId = table.Column<Guid>(type: "uuid", nullable: true),
                    DedupeKey = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Disputed = table.Column<bool>(type: "boolean", nullable: false),
                    Evidence = table.Column<List<string>>(type: "text[]", nullable: false),
                    Explanation = table.Column<string>(type: "character varying(1200)", maxLength: 1200, nullable: false),
                    Hosts = table.Column<List<Guid>>(type: "uuid[]", nullable: false),
                    Key = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    Kind = table.Column<short>(type: "smallint", nullable: false),
                    Muted = table.Column<bool>(type: "boolean", nullable: false),
                    New = table.Column<bool>(type: "boolean", nullable: false),
                    Recommendation = table.Column<string>(type: "character varying(600)", maxLength: 600, nullable: false),
                    Sensors = table.Column<List<Guid>>(type: "uuid[]", nullable: false),
                    Severity = table.Column<short>(type: "smallint", nullable: false),
                    Title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Verified = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ai_findings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ai_findings_ai_analysis_runs_RunId",
                        column: x => x.RunId,
                        principalTable: "ai_analysis_runs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ai_analysis_runs_At",
                table: "ai_analysis_runs",
                column: "At");

            migrationBuilder.CreateIndex(
                name: "IX_ai_analysis_runs_State",
                table: "ai_analysis_runs",
                column: "State");

            migrationBuilder.CreateIndex(
                name: "IX_ai_analysis_runs_WorkId",
                table: "ai_analysis_runs",
                column: "WorkId");

            migrationBuilder.CreateIndex(
                name: "IX_ai_findings_DedupeKey",
                table: "ai_findings",
                column: "DedupeKey");

            migrationBuilder.CreateIndex(
                name: "IX_ai_findings_RunId",
                table: "ai_findings",
                column: "RunId");
        }
    }
}
