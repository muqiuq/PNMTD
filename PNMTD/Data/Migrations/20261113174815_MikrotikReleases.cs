using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PNMTD.Data.Migrations
{
    /// <inheritdoc />
    public partial class MikrotikReleases : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "mikrotik_releases",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Version = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Channel = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    PublishedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Changelog = table.Column<string>(type: "text", nullable: false),
                    FirstSeenAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Severity = table.Column<short>(type: "smallint", nullable: false),
                    ZeroDay = table.Column<bool>(type: "boolean", nullable: false),
                    Exploited = table.Column<bool>(type: "boolean", nullable: false),
                    RemoteWithoutCredentials = table.Column<bool>(type: "boolean", nullable: false),
                    Cves = table.Column<List<string>>(type: "text[]", nullable: false),
                    Summary = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    AssessedBy = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    AssessedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    WorkId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_mikrotik_releases", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_mikrotik_releases_Version_Channel",
                table: "mikrotik_releases",
                columns: new[] { "Version", "Channel" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "mikrotik_releases");
        }
    }
}
