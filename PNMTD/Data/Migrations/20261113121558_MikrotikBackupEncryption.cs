using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PNMTD.Data.Migrations
{
    /// <inheritdoc />
    public partial class MikrotikBackupEncryption : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "BackupKeyId",
                table: "mikrotik_settings",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "DefaultEncryptBackups",
                table: "mikrotik_settings",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "EncryptBackups",
                table: "mikrotik_routers",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "EncryptedWith",
                table: "mikrotik_backups",
                type: "text",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "mikrotik_backup_keys",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<string>(type: "text", nullable: false),
                    Recipient = table.Column<string>(type: "text", nullable: false),
                    Fingerprint = table.Column<string>(type: "text", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedByName = table.Column<string>(type: "text", nullable: true),
                    RetiredAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_mikrotik_backup_keys", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_mikrotik_backup_keys_Fingerprint",
                table: "mikrotik_backup_keys",
                column: "Fingerprint",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "mikrotik_backup_keys");

            migrationBuilder.DropColumn(
                name: "BackupKeyId",
                table: "mikrotik_settings");

            migrationBuilder.DropColumn(
                name: "DefaultEncryptBackups",
                table: "mikrotik_settings");

            migrationBuilder.DropColumn(
                name: "EncryptBackups",
                table: "mikrotik_routers");

            migrationBuilder.DropColumn(
                name: "EncryptedWith",
                table: "mikrotik_backups");
        }
    }
}
