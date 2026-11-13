using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PNMTD.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddWireGuardTunnels : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "wg_peer_access",
                columns: table => new
                {
                    PeerId = table.Column<Guid>(type: "uuid", nullable: false),
                    RouterSensorId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_wg_peer_access", x => new { x.PeerId, x.RouterSensorId });
                    table.ForeignKey(
                        name: "FK_wg_peer_access_sensors_RouterSensorId",
                        column: x => x.RouterSensorId,
                        principalTable: "sensors",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_wg_peer_access_wg_peers_PeerId",
                        column: x => x.PeerId,
                        principalTable: "wg_peers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_wg_peer_access_RouterSensorId",
                table: "wg_peer_access",
                column: "RouterSensorId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "wg_peer_access");
        }
    }
}
