using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PNMTD.Migrations
{
    /// <inheritdoc />
    public partial class DoneZoneHostLink : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "HostId",
                table: "dnszones",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_dnszones_HostId",
                table: "dnszones",
                column: "HostId");

            migrationBuilder.AddForeignKey(
                name: "FK_dnszones_hosts_HostId",
                table: "dnszones",
                column: "HostId",
                principalTable: "hosts",
                principalColumn: "Id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_dnszones_hosts_HostId",
                table: "dnszones");

            migrationBuilder.DropIndex(
                name: "IX_dnszones_HostId",
                table: "dnszones");

            migrationBuilder.DropColumn(
                name: "HostId",
                table: "dnszones");
        }
    }
}
