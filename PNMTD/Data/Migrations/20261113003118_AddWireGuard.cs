using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PNMTD.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddWireGuard : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "ScriptGeneratedAt",
                table: "mikrotik_routers",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "WgPeerId",
                table: "mikrotik_routers",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "wg_network",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false),
                    Prefix = table.Column<string>(type: "text", nullable: false),
                    PublicKey = table.Column<string>(type: "text", nullable: false),
                    PrivateKeyProtected = table.Column<string>(type: "text", nullable: false),
                    KeepaliveSeconds = table.Column<int>(type: "integer", nullable: false),
                    Mtu = table.Column<int>(type: "integer", nullable: false),
                    StaleHandshakeSeconds = table.Column<int>(type: "integer", nullable: false),
                    ExternalHost = table.Column<string>(type: "text", nullable: true),
                    ExternalPort = table.Column<int>(type: "integer", nullable: true),
                    ManagementPorts = table.Column<int[]>(type: "integer[]", nullable: false),
                    SocksSecretProtected = table.Column<string>(type: "text", nullable: false),
                    ScriptSettingsChangedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    GatewaySeenAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    GatewayStartedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    GatewayVersion = table.Column<string>(type: "text", nullable: true),
                    GatewayError = table.Column<string>(type: "text", nullable: true),
                    ChangedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ChangedBy = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_wg_network", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "wg_peers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<short>(type: "smallint", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    Notes = table.Column<string>(type: "text", nullable: true),
                    Address = table.Column<string>(type: "text", nullable: false),
                    PublicKey = table.Column<string>(type: "text", nullable: false),
                    PrivateKeyProtected = table.Column<string>(type: "text", nullable: false),
                    KeysCreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    KeysCreatedBy = table.Column<Guid>(type: "uuid", nullable: true),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false),
                    SensorId = table.Column<Guid>(type: "uuid", nullable: true),
                    OwnerUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    AccessAllRouters = table.Column<bool>(type: "boolean", nullable: false),
                    ExpiresAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_wg_peers", x => x.Id);
                    table.ForeignKey(
                        name: "FK_wg_peers_sensors_SensorId",
                        column: x => x.SensorId,
                        principalTable: "sensors",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_wg_peers_users_OwnerUserId",
                        column: x => x.OwnerUserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "wg_peer_status",
                columns: table => new
                {
                    PeerId = table.Column<Guid>(type: "uuid", nullable: false),
                    Endpoint = table.Column<string>(type: "text", nullable: true),
                    LatestHandshake = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    RxBytes = table.Column<long>(type: "bigint", nullable: false),
                    TxBytes = table.Column<long>(type: "bigint", nullable: false),
                    EndpointChanges24h = table.Column<int>(type: "integer", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_wg_peer_status", x => x.PeerId);
                    table.ForeignKey(
                        name: "FK_wg_peer_status_wg_peers_PeerId",
                        column: x => x.PeerId,
                        principalTable: "wg_peers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_mikrotik_routers_WgPeerId",
                table: "mikrotik_routers",
                column: "WgPeerId");

            migrationBuilder.CreateIndex(
                name: "IX_wg_peers_Address",
                table: "wg_peers",
                column: "Address",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_wg_peers_OwnerUserId",
                table: "wg_peers",
                column: "OwnerUserId");

            migrationBuilder.CreateIndex(
                name: "IX_wg_peers_PublicKey",
                table: "wg_peers",
                column: "PublicKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_wg_peers_SensorId",
                table: "wg_peers",
                column: "SensorId");

            migrationBuilder.AddForeignKey(
                name: "FK_mikrotik_routers_wg_peers_WgPeerId",
                table: "mikrotik_routers",
                column: "WgPeerId",
                principalTable: "wg_peers",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_mikrotik_routers_wg_peers_WgPeerId",
                table: "mikrotik_routers");

            migrationBuilder.DropTable(
                name: "wg_network");

            migrationBuilder.DropTable(
                name: "wg_peer_status");

            migrationBuilder.DropTable(
                name: "wg_peers");

            migrationBuilder.DropIndex(
                name: "IX_mikrotik_routers_WgPeerId",
                table: "mikrotik_routers");

            migrationBuilder.DropColumn(
                name: "ScriptGeneratedAt",
                table: "mikrotik_routers");

            migrationBuilder.DropColumn(
                name: "WgPeerId",
                table: "mikrotik_routers");
        }
    }
}
