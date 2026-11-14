using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PNMTD.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddZabbixSensors : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "zabbix_sensors",
                columns: table => new
                {
                    SensorId = table.Column<Guid>(type: "uuid", nullable: false),
                    ZabbixHostId = table.Column<long>(type: "bigint", nullable: false),
                    MinSeverity = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    CountAcknowledged = table.Column<bool>(type: "boolean", nullable: false),
                    CountSuppressed = table.Column<bool>(type: "boolean", nullable: false),
                    UnavailableFails = table.Column<bool>(type: "boolean", nullable: false),
                    LastCode = table.Column<int>(type: "integer", nullable: true),
                    LastProblemIds = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    Created = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_zabbix_sensors", x => x.SensorId);
                    table.ForeignKey(
                        name: "FK_zabbix_sensors_sensors_SensorId",
                        column: x => x.SensorId,
                        principalTable: "sensors",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_zabbix_sensors_ZabbixHostId",
                table: "zabbix_sensors",
                column: "ZabbixHostId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "zabbix_sensors");
        }
    }
}
