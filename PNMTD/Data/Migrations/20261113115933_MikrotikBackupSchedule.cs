using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PNMTD.Data.Migrations
{
    /// <inheritdoc />
    public partial class MikrotikBackupSchedule : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "DefaultBackupIntervalHours",
                table: "mikrotik_settings",
                type: "integer",
                nullable: true);

            migrationBuilder.AlterColumn<int>(
                name: "BackupIntervalHours",
                table: "mikrotik_routers",
                type: "integer",
                nullable: true,
                oldClrType: typeof(int),
                oldType: "integer");

            // An installation that ran before keeps the daily schedule for new routers; null would be off.
            migrationBuilder.Sql("UPDATE mikrotik_settings SET \"DefaultBackupIntervalHours\" = 24");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DefaultBackupIntervalHours",
                table: "mikrotik_settings");

            // Back to a schedule for every router: off becomes the old default.
            migrationBuilder.Sql("UPDATE mikrotik_routers SET \"BackupIntervalHours\" = 24 WHERE \"BackupIntervalHours\" IS NULL");

            migrationBuilder.AlterColumn<int>(
                name: "BackupIntervalHours",
                table: "mikrotik_routers",
                type: "integer",
                nullable: false,
                defaultValue: 0,
                oldClrType: typeof(int),
                oldType: "integer",
                oldNullable: true);
        }
    }
}
