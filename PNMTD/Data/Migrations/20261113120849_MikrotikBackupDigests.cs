using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PNMTD.Data.Migrations
{
    /// <inheritdoc />
    public partial class MikrotikBackupDigests : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "Sha256",
                table: "mikrotik_backups",
                newName: "ContentHash");

            migrationBuilder.AddColumn<string>(
                name: "BackupDigestKeyProtected",
                table: "mikrotik_settings",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<byte[]>(
                name: "LastExportLineDigests",
                table: "mikrotik_routers",
                type: "bytea",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "BackupDigestKeyProtected",
                table: "mikrotik_settings");

            migrationBuilder.DropColumn(
                name: "LastExportLineDigests",
                table: "mikrotik_routers");

            migrationBuilder.RenameColumn(
                name: "ContentHash",
                table: "mikrotik_backups",
                newName: "Sha256");
        }
    }
}
