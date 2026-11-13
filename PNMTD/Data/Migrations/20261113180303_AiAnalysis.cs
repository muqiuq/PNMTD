using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PNMTD.Data.Migrations
{
    /// <inheritdoc />
    public partial class AiAnalysis : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ai_analysis_runs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    At = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Trigger = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    SnapshotHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Mapping = table.Column<string>(type: "jsonb", nullable: false),
                    State = table.Column<short>(type: "smallint", nullable: false),
                    Status = table.Column<short>(type: "smallint", nullable: true),
                    Summary = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    WorkId = table.Column<Guid>(type: "uuid", nullable: true),
                    CompletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Error = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    Withheld = table.Column<int>(type: "integer", nullable: false),
                    GuardFlagged = table.Column<bool>(type: "boolean", nullable: false),
                    SnapshotChars = table.Column<int>(type: "integer", nullable: false)
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
                    Key = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    DedupeKey = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Kind = table.Column<short>(type: "smallint", nullable: false),
                    Severity = table.Column<short>(type: "smallint", nullable: false),
                    Title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Explanation = table.Column<string>(type: "character varying(1200)", maxLength: 1200, nullable: false),
                    Recommendation = table.Column<string>(type: "character varying(600)", maxLength: 600, nullable: false),
                    Confidence = table.Column<double>(type: "double precision", nullable: false),
                    Sensors = table.Column<List<Guid>>(type: "uuid[]", nullable: false),
                    Hosts = table.Column<List<Guid>>(type: "uuid[]", nullable: false),
                    Evidence = table.Column<List<string>>(type: "text[]", nullable: false),
                    Verified = table.Column<bool>(type: "boolean", nullable: false),
                    Disputed = table.Column<bool>(type: "boolean", nullable: false),
                    New = table.Column<bool>(type: "boolean", nullable: false),
                    Muted = table.Column<bool>(type: "boolean", nullable: false),
                    DecisionId = table.Column<Guid>(type: "uuid", nullable: true)
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

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ai_findings");

            migrationBuilder.DropTable(
                name: "ai_analysis_runs");
        }
    }
}
