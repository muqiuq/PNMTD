using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PNMTD.Migrations
{
    /// <inheritdoc />
    public partial class DnsZones : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "dnszones",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    ZoneName = table.Column<string>(type: "TEXT", nullable: false),
                    ZoneFileContent = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_dnszones", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "dnszoneentries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Ignore = table.Column<bool>(type: "INTEGER", nullable: false),
                    DnsZoneId = table.Column<Guid>(type: "TEXT", nullable: false),
                    TTL = table.Column<int>(type: "INTEGER", nullable: false),
                    Type = table.Column<string>(type: "TEXT", nullable: false),
                    ReferenceValue = table.Column<string>(type: "TEXT", nullable: false),
                    ActualValue = table.Column<string>(type: "TEXT", nullable: false),
                    Updated = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_dnszoneentries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_dnszoneentries_dnszones_DnsZoneId",
                        column: x => x.DnsZoneId,
                        principalTable: "dnszones",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "dnszonelogentries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Created = table.Column<DateTime>(type: "TEXT", nullable: false),
                    DnsZoneId = table.Column<Guid>(type: "TEXT", nullable: true),
                    DnsZoneEntryId = table.Column<Guid>(type: "TEXT", nullable: true),
                    EntryType = table.Column<int>(type: "INTEGER", nullable: false),
                    Message = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_dnszonelogentries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_dnszonelogentries_dnszoneentries_DnsZoneEntryId",
                        column: x => x.DnsZoneEntryId,
                        principalTable: "dnszoneentries",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_dnszonelogentries_dnszones_DnsZoneId",
                        column: x => x.DnsZoneId,
                        principalTable: "dnszones",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateIndex(
                name: "IX_dnszoneentries_DnsZoneId",
                table: "dnszoneentries",
                column: "DnsZoneId");

            migrationBuilder.CreateIndex(
                name: "IX_dnszonelogentries_DnsZoneEntryId",
                table: "dnszonelogentries",
                column: "DnsZoneEntryId");

            migrationBuilder.CreateIndex(
                name: "IX_dnszonelogentries_DnsZoneId",
                table: "dnszonelogentries",
                column: "DnsZoneId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "dnszonelogentries");

            migrationBuilder.DropTable(
                name: "dnszoneentries");

            migrationBuilder.DropTable(
                name: "dnszones");
        }
    }
}
