using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PNMTD.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddRedmine : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "redmine_issues",
                columns: table => new
                {
                    IssueId = table.Column<int>(type: "integer", nullable: false),
                    ProjectId = table.Column<int>(type: "integer", nullable: false),
                    ProjectName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    TrackerName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    StatusName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    IsClosed = table.Column<bool>(type: "boolean", nullable: false),
                    PriorityId = table.Column<int>(type: "integer", nullable: true),
                    PriorityName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    Subject = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    AssignedToName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    UpdatedOn = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    DueDate = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: true),
                    FetchedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Missing = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_redmine_issues", x => x.IssueId);
                });

            migrationBuilder.CreateTable(
                name: "redmine_links",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    IssueId = table.Column<int>(type: "integer", nullable: false),
                    PageId = table.Column<Guid>(type: "uuid", nullable: true),
                    LocationId = table.Column<Guid>(type: "uuid", nullable: true),
                    HostId = table.Column<Guid>(type: "uuid", nullable: true),
                    SensorId = table.Column<Guid>(type: "uuid", nullable: true),
                    Note = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedById = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedByName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_redmine_links", x => x.Id);
                    table.CheckConstraint("CK_redmine_links_one_target", "num_nonnulls(\"PageId\", \"LocationId\", \"HostId\", \"SensorId\") = 1");
                    table.ForeignKey(
                        name: "FK_redmine_links_hosts_HostId",
                        column: x => x.HostId,
                        principalTable: "hosts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_redmine_links_kb_pages_PageId",
                        column: x => x.PageId,
                        principalTable: "kb_pages",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_redmine_links_locations_LocationId",
                        column: x => x.LocationId,
                        principalTable: "locations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_redmine_links_sensors_SensorId",
                        column: x => x.SensorId,
                        principalTable: "sensors",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_redmine_links_HostId_IssueId",
                table: "redmine_links",
                columns: new[] { "HostId", "IssueId" },
                unique: true,
                filter: "\"HostId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_redmine_links_IssueId",
                table: "redmine_links",
                column: "IssueId");

            migrationBuilder.CreateIndex(
                name: "IX_redmine_links_LocationId_IssueId",
                table: "redmine_links",
                columns: new[] { "LocationId", "IssueId" },
                unique: true,
                filter: "\"LocationId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_redmine_links_PageId_IssueId",
                table: "redmine_links",
                columns: new[] { "PageId", "IssueId" },
                unique: true,
                filter: "\"PageId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_redmine_links_SensorId_IssueId",
                table: "redmine_links",
                columns: new[] { "SensorId", "IssueId" },
                unique: true,
                filter: "\"SensorId\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "redmine_issues");

            migrationBuilder.DropTable(
                name: "redmine_links");
        }
    }
}
