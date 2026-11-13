using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PNMTD.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddDiagnostics : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "diagnostic_sets",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Description = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_diagnostic_sets", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "diagnostic_assignments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SetId = table.Column<Guid>(type: "uuid", nullable: false),
                    TargetSensorId = table.Column<Guid>(type: "uuid", nullable: false),
                    SensorId = table.Column<Guid>(type: "uuid", nullable: false),
                    Mode = table.Column<int>(type: "integer", nullable: false),
                    IntervalMinutes = table.Column<int>(type: "integer", nullable: false),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_diagnostic_assignments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_diagnostic_assignments_diagnostic_sets_SetId",
                        column: x => x.SetId,
                        principalTable: "diagnostic_sets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_diagnostic_assignments_sensors_SensorId",
                        column: x => x.SensorId,
                        principalTable: "sensors",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_diagnostic_assignments_sensors_TargetSensorId",
                        column: x => x.TargetSensorId,
                        principalTable: "sensors",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "diagnostic_checks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SetId = table.Column<Guid>(type: "uuid", nullable: false),
                    Order = table.Column<int>(type: "integer", nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Type = table.Column<int>(type: "integer", nullable: false),
                    Parameters = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_diagnostic_checks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_diagnostic_checks_diagnostic_sets_SetId",
                        column: x => x.SetId,
                        principalTable: "diagnostic_sets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "diagnostic_runs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AssignmentId = table.Column<Guid>(type: "uuid", nullable: false),
                    SensorId = table.Column<Guid>(type: "uuid", nullable: false),
                    Trigger = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    RequestedBy = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    StartedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    FinishedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Outcome = table.Column<int>(type: "integer", nullable: true),
                    Summary = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    Results = table.Column<string>(type: "jsonb", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_diagnostic_runs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_diagnostic_runs_diagnostic_assignments_AssignmentId",
                        column: x => x.AssignmentId,
                        principalTable: "diagnostic_assignments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_diagnostic_assignments_SensorId",
                table: "diagnostic_assignments",
                column: "SensorId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_diagnostic_assignments_SetId",
                table: "diagnostic_assignments",
                column: "SetId");

            migrationBuilder.CreateIndex(
                name: "IX_diagnostic_assignments_TargetSensorId",
                table: "diagnostic_assignments",
                column: "TargetSensorId");

            migrationBuilder.CreateIndex(
                name: "IX_diagnostic_checks_SetId",
                table: "diagnostic_checks",
                column: "SetId");

            migrationBuilder.CreateIndex(
                name: "IX_diagnostic_runs_AssignmentId_StartedAt",
                table: "diagnostic_runs",
                columns: new[] { "AssignmentId", "StartedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_diagnostic_runs_StartedAt",
                table: "diagnostic_runs",
                column: "StartedAt");

            migrationBuilder.CreateIndex(
                name: "IX_diagnostic_sets_Name",
                table: "diagnostic_sets",
                column: "Name",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "diagnostic_checks");

            migrationBuilder.DropTable(
                name: "diagnostic_runs");

            migrationBuilder.DropTable(
                name: "diagnostic_assignments");

            migrationBuilder.DropTable(
                name: "diagnostic_sets");
        }
    }
}
