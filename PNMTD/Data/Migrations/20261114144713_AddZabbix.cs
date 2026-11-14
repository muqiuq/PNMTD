using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PNMTD.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddZabbix : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "zabbix_hosts",
                columns: table => new
                {
                    HostId = table.Column<long>(type: "bigint", nullable: false),
                    TechnicalName = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    VisibleName = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    Monitored = table.Column<bool>(type: "boolean", nullable: false),
                    InMaintenance = table.Column<bool>(type: "boolean", nullable: false),
                    Groups = table.Column<List<string>>(type: "text[]", nullable: false),
                    Addresses = table.Column<List<string>>(type: "text[]", nullable: false),
                    Interfaces = table.Column<string>(type: "jsonb", nullable: false),
                    Availability = table.Column<int>(type: "integer", nullable: false),
                    SyncedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    GoneAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_zabbix_hosts", x => x.HostId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_zabbix_hosts_TechnicalName",
                table: "zabbix_hosts",
                column: "TechnicalName");

            migrationBuilder.CreateIndex(
                name: "IX_zabbix_hosts_VisibleName",
                table: "zabbix_hosts",
                column: "VisibleName");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "zabbix_hosts");
        }
    }
}
