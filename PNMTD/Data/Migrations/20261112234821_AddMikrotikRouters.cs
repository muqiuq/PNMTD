using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PNMTD.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddMikrotikRouters : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "mikrotik_routers",
                columns: table => new
                {
                    SensorId = table.Column<Guid>(type: "uuid", nullable: false),
                    Mode = table.Column<short>(type: "smallint", nullable: false),
                    Address = table.Column<string>(type: "text", nullable: true),
                    SshPort = table.Column<int>(type: "integer", nullable: false),
                    PnmtSourceAddress = table.Column<string>(type: "text", nullable: true),
                    ManagementAccess = table.Column<bool>(type: "boolean", nullable: false),
                    IncludeSecrets = table.Column<bool>(type: "boolean", nullable: false),
                    Diagnostics = table.Column<bool>(type: "boolean", nullable: false),
                    BackupIntervalHours = table.Column<int>(type: "integer", nullable: false),
                    SetupState = table.Column<short>(type: "smallint", nullable: false),
                    ScriptChangedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    HostKeyType = table.Column<string>(type: "text", nullable: true),
                    HostKeyFingerprint = table.Column<string>(type: "text", nullable: true),
                    HostKeyPinnedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    PendingHostKeyType = table.Column<string>(type: "text", nullable: true),
                    PendingHostKeyFingerprint = table.Column<string>(type: "text", nullable: true),
                    PendingHostKeySeenAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Identity = table.Column<string>(type: "text", nullable: true),
                    Model = table.Column<string>(type: "text", nullable: true),
                    BoardName = table.Column<string>(type: "text", nullable: true),
                    SerialNumber = table.Column<string>(type: "text", nullable: true),
                    Architecture = table.Column<string>(type: "text", nullable: true),
                    Version = table.Column<string>(type: "text", nullable: true),
                    Channel = table.Column<string>(type: "text", nullable: true),
                    FirmwareCurrent = table.Column<string>(type: "text", nullable: true),
                    FirmwareUpgrade = table.Column<string>(type: "text", nullable: true),
                    BootedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Reading = table.Column<string>(type: "jsonb", nullable: true),
                    LastPollAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastSuccessAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    FailingSince = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastError = table.Column<string>(type: "text", nullable: true),
                    LastErrorCode = table.Column<int>(type: "integer", nullable: true),
                    NotifiedUpdateVersion = table.Column<string>(type: "text", nullable: true),
                    Created = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_mikrotik_routers", x => x.SensorId);
                    table.ForeignKey(
                        name: "FK_mikrotik_routers_sensors_SensorId",
                        column: x => x.SensorId,
                        principalTable: "sensors",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "mikrotik_settings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false),
                    Tag = table.Column<string>(type: "text", nullable: false),
                    LatestVersions = table.Column<string>(type: "jsonb", nullable: true),
                    VersionsCheckedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    VersionCheckError = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_mikrotik_settings", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "mikrotik_ssh_keys",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    PublicKey = table.Column<string>(type: "text", nullable: false),
                    Comment = table.Column<string>(type: "text", nullable: false),
                    PrivateKeyProtected = table.Column<string>(type: "text", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uuid", nullable: true),
                    RetiredAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_mikrotik_ssh_keys", x => x.Id);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "mikrotik_routers");

            migrationBuilder.DropTable(
                name: "mikrotik_settings");

            migrationBuilder.DropTable(
                name: "mikrotik_ssh_keys");
        }
    }
}
