using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PNMTD.Migrations
{
    /// <inheritdoc />
    public partial class DnsZoneChanges : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_dnszonelogentries_dnszoneentries_DnsZoneEntryId",
                table: "dnszonelogentries");

            migrationBuilder.DropIndex(
                name: "IX_dnszonelogentries_DnsZoneEntryId",
                table: "dnszonelogentries");

            migrationBuilder.DropColumn(
                name: "DnsZoneEntryId",
                table: "dnszonelogentries");

            migrationBuilder.AddColumn<DateTime>(
                name: "LastChecked",
                table: "dnszones",
                type: "TEXT",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified));

            migrationBuilder.AddColumn<bool>(
                name: "RecordsMatch",
                table: "dnszones",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LastChecked",
                table: "dnszones");

            migrationBuilder.DropColumn(
                name: "RecordsMatch",
                table: "dnszones");

            migrationBuilder.AddColumn<Guid>(
                name: "DnsZoneEntryId",
                table: "dnszonelogentries",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_dnszonelogentries_DnsZoneEntryId",
                table: "dnszonelogentries",
                column: "DnsZoneEntryId");

            migrationBuilder.AddForeignKey(
                name: "FK_dnszonelogentries_dnszoneentries_DnsZoneEntryId",
                table: "dnszonelogentries",
                column: "DnsZoneEntryId",
                principalTable: "dnszoneentries",
                principalColumn: "Id");
        }
    }
}
