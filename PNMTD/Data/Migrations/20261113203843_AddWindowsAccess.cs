using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PNMTD.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddWindowsAccess : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "AuditGaps",
                table: "windows_agents",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "RemoteAccessSummary",
                table: "windows_agents",
                type: "character varying(300)",
                maxLength: 300,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "windows_changes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SensorId = table.Column<Guid>(type: "uuid", nullable: false),
                    Section = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    At = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Summary = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    Details = table.Column<string>(type: "jsonb", nullable: true),
                    Notable = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_windows_changes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_windows_changes_sensors_SensorId",
                        column: x => x.SensorId,
                        principalTable: "sensors",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "windows_logins",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SensorId = table.Column<Guid>(type: "uuid", nullable: false),
                    At = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LastAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Count = table.Column<int>(type: "integer", nullable: false),
                    Outcome = table.Column<int>(type: "integer", nullable: false),
                    FailureReason = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    User = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    Domain = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    IsAdmin = table.Column<bool>(type: "boolean", nullable: false),
                    Kind = table.Column<int>(type: "integer", nullable: false),
                    AuthPackage = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    SourceAddress = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    SourcePort = table.Column<int>(type: "integer", nullable: true),
                    Workstation = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    Elevated = table.Column<bool>(type: "boolean", nullable: true),
                    LogonId = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    LoggedOffAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Channel = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    RecordId = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_windows_logins", x => x.Id);
                    table.ForeignKey(
                        name: "FK_windows_logins_sensors_SensorId",
                        column: x => x.SensorId,
                        principalTable: "sensors",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_windows_changes_At",
                table: "windows_changes",
                column: "At");

            migrationBuilder.CreateIndex(
                name: "IX_windows_changes_SensorId_At",
                table: "windows_changes",
                columns: new[] { "SensorId", "At" });

            migrationBuilder.CreateIndex(
                name: "IX_windows_logins_At",
                table: "windows_logins",
                column: "At");

            migrationBuilder.CreateIndex(
                name: "IX_windows_logins_SensorId_At",
                table: "windows_logins",
                columns: new[] { "SensorId", "At" });

            migrationBuilder.CreateIndex(
                name: "IX_windows_logins_SensorId_Channel_RecordId",
                table: "windows_logins",
                columns: new[] { "SensorId", "Channel", "RecordId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "windows_changes");

            migrationBuilder.DropTable(
                name: "windows_logins");

            migrationBuilder.DropColumn(
                name: "AuditGaps",
                table: "windows_agents");

            migrationBuilder.DropColumn(
                name: "RemoteAccessSummary",
                table: "windows_agents");
        }
    }
}
