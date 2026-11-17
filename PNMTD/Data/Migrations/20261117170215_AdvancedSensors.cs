using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PNMTD.Data.Migrations
{
    /// <inheritdoc />
    public partial class AdvancedSensors : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "advanced_fields",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SensorId = table.Column<Guid>(type: "uuid", nullable: false),
                    Order = table.Column<int>(type: "integer", nullable: false),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Path = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    Format = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Unit = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    Decimals = table.Column<int>(type: "integer", nullable: true),
                    Pinned = table.Column<bool>(type: "boolean", nullable: false),
                    Chart = table.Column<bool>(type: "boolean", nullable: false),
                    Created = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_advanced_fields", x => x.Id);
                    table.ForeignKey(
                        name: "FK_advanced_fields_sensors_SensorId",
                        column: x => x.SensorId,
                        principalTable: "sensors",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "advanced_reports",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SensorId = table.Column<Guid>(type: "uuid", nullable: false),
                    ReceivedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LastReceivedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Repeats = table.Column<int>(type: "integer", nullable: false),
                    Source = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    Bytes = table.Column<int>(type: "integer", nullable: false),
                    Sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Body = table.Column<string>(type: "text", nullable: true),
                    Valid = table.Column<bool>(type: "boolean", nullable: false),
                    ErrorKind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    Error = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    Status = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    Message = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    MeasuredAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    EvaluatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    EvaluationAttempts = table.Column<short>(type: "smallint", nullable: false),
                    Code = table.Column<int>(type: "integer", nullable: true),
                    Failed = table.Column<int>(type: "integer", nullable: false),
                    Warnings = table.Column<int>(type: "integer", nullable: false),
                    Results = table.Column<string>(type: "jsonb", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_advanced_reports", x => x.Id);
                    table.ForeignKey(
                        name: "FK_advanced_reports_sensors_SensorId",
                        column: x => x.SensorId,
                        principalTable: "sensors",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "advanced_rules",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SensorId = table.Column<Guid>(type: "uuid", nullable: false),
                    Order = table.Column<int>(type: "integer", nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false),
                    Note = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    Path = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    Operator = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    Operand = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    Operand2 = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    IgnoreCase = table.Column<bool>(type: "boolean", nullable: false),
                    Quantifier = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    WhenMissing = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    Severity = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    Created = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_advanced_rules", x => x.Id);
                    table.ForeignKey(
                        name: "FK_advanced_rules_sensors_SensorId",
                        column: x => x.SensorId,
                        principalTable: "sensors",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "advanced_samples",
                columns: table => new
                {
                    SensorId = table.Column<Guid>(type: "uuid", nullable: false),
                    FieldId = table.Column<Guid>(type: "uuid", nullable: false),
                    Minute = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Min = table.Column<double>(type: "double precision", nullable: false),
                    Max = table.Column<double>(type: "double precision", nullable: false),
                    Last = table.Column<double>(type: "double precision", nullable: false),
                    Count = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_advanced_samples", x => new { x.SensorId, x.FieldId, x.Minute });
                    table.ForeignKey(
                        name: "FK_advanced_samples_advanced_fields_FieldId",
                        column: x => x.FieldId,
                        principalTable: "advanced_fields",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_advanced_samples_sensors_SensorId",
                        column: x => x.SensorId,
                        principalTable: "sensors",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "advanced_sensors",
                columns: table => new
                {
                    SensorId = table.Column<Guid>(type: "uuid", nullable: false),
                    NamesPeople = table.Column<bool>(type: "boolean", nullable: false),
                    LastReportId = table.Column<Guid>(type: "uuid", nullable: true),
                    LastReportAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastValidReportAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastSource = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    LastVerdictKey = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    LastCode = table.Column<int>(type: "integer", nullable: true),
                    LastMessage = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    LastEvaluatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    RuleStates = table.Column<string>(type: "jsonb", nullable: true),
                    ReevaluateRequestedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Created = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_advanced_sensors", x => x.SensorId);
                    table.ForeignKey(
                        name: "FK_advanced_sensors_advanced_reports_LastReportId",
                        column: x => x.LastReportId,
                        principalTable: "advanced_reports",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_advanced_sensors_sensors_SensorId",
                        column: x => x.SensorId,
                        principalTable: "sensors",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_advanced_fields_SensorId_Order",
                table: "advanced_fields",
                columns: new[] { "SensorId", "Order" });

            migrationBuilder.CreateIndex(
                name: "IX_advanced_reports_queue",
                table: "advanced_reports",
                columns: new[] { "SensorId", "ReceivedAt" },
                filter: "\"EvaluatedAt\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_advanced_reports_SensorId_ReceivedAt",
                table: "advanced_reports",
                columns: new[] { "SensorId", "ReceivedAt" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "IX_advanced_rules_SensorId_Order",
                table: "advanced_rules",
                columns: new[] { "SensorId", "Order" });

            migrationBuilder.CreateIndex(
                name: "IX_advanced_samples_FieldId",
                table: "advanced_samples",
                column: "FieldId");

            migrationBuilder.CreateIndex(
                name: "IX_advanced_samples_Minute",
                table: "advanced_samples",
                column: "Minute");

            migrationBuilder.CreateIndex(
                name: "IX_advanced_sensors_LastReportId",
                table: "advanced_sensors",
                column: "LastReportId");

            migrationBuilder.CreateIndex(
                name: "IX_advanced_sensors_ReevaluateRequestedAt",
                table: "advanced_sensors",
                column: "ReevaluateRequestedAt",
                filter: "\"ReevaluateRequestedAt\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "advanced_rules");

            migrationBuilder.DropTable(
                name: "advanced_samples");

            migrationBuilder.DropTable(
                name: "advanced_sensors");

            migrationBuilder.DropTable(
                name: "advanced_fields");

            migrationBuilder.DropTable(
                name: "advanced_reports");
        }
    }
}
