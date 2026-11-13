using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PNMTD.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddMikrotikBackups : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "BackupFailures",
                table: "mikrotik_routers",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "LastBackupError",
                table: "mikrotik_routers",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "LastExportAt",
                table: "mikrotik_routers",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "LastExportAttemptAt",
                table: "mikrotik_routers",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LastExportHash",
                table: "mikrotik_routers",
                type: "text",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "mikrotik_backups",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SensorId = table.Column<Guid>(type: "uuid", nullable: true),
                    RouterName = table.Column<string>(type: "text", nullable: false),
                    Number = table.Column<int>(type: "integer", nullable: false),
                    Kind = table.Column<short>(type: "smallint", nullable: false),
                    Trigger = table.Column<short>(type: "smallint", nullable: false),
                    JobId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedByName = table.Column<string>(type: "text", nullable: true),
                    RouterOsVersion = table.Column<string>(type: "text", nullable: true),
                    Content = table.Column<byte[]>(type: "bytea", nullable: false),
                    Size = table.Column<long>(type: "bigint", nullable: false),
                    LineCount = table.Column<int>(type: "integer", nullable: false),
                    Sha256 = table.Column<string>(type: "text", nullable: false),
                    IncludesSecrets = table.Column<bool>(type: "boolean", nullable: false),
                    LinesAdded = table.Column<int>(type: "integer", nullable: true),
                    LinesRemoved = table.Column<int>(type: "integer", nullable: true),
                    Encrypted = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_mikrotik_backups", x => x.Id);
                    table.ForeignKey(
                        name: "FK_mikrotik_backups_sensors_SensorId",
                        column: x => x.SensorId,
                        principalTable: "sensors",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "IX_mikrotik_backups_CreatedAt",
                table: "mikrotik_backups",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_mikrotik_backups_SensorId_CreatedAt",
                table: "mikrotik_backups",
                columns: new[] { "SensorId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_mikrotik_backups_SensorId_Number",
                table: "mikrotik_backups",
                columns: new[] { "SensorId", "Number" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "mikrotik_backups");

            migrationBuilder.DropColumn(
                name: "BackupFailures",
                table: "mikrotik_routers");

            migrationBuilder.DropColumn(
                name: "LastBackupError",
                table: "mikrotik_routers");

            migrationBuilder.DropColumn(
                name: "LastExportAt",
                table: "mikrotik_routers");

            migrationBuilder.DropColumn(
                name: "LastExportAttemptAt",
                table: "mikrotik_routers");

            migrationBuilder.DropColumn(
                name: "LastExportHash",
                table: "mikrotik_routers");
        }
    }
}
