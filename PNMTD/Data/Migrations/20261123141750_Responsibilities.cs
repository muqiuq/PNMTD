using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PNMTD.Data.Migrations
{
    /// <inheritdoc />
    /// <summary>
    /// docs/responsibilities-plan.md §10.1–§10.5 (P1): responsibilities with a responsible and a deputy (a deleted user's place becomes
    /// empty), their links to exactly one page, location or host (going with either side), the one row of the global responsible and
    /// deputy, and each responsibility's history.
    /// </summary>
    public partial class Responsibilities : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "responsibilities",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    NormalizedName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Summary = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    Description = table.Column<string>(type: "text", nullable: false),
                    PlainText = table.Column<string>(type: "text", nullable: false),
                    Revision = table.Column<int>(type: "integer", nullable: false),
                    ResponsibleId = table.Column<Guid>(type: "uuid", nullable: true),
                    DeputyId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedByName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedBy = table.Column<Guid>(type: "uuid", nullable: true),
                    UpdatedByName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_responsibilities", x => x.Id);
                    table.CheckConstraint("CK_responsibilities_two_people", "\"ResponsibleId\" IS NULL OR \"DeputyId\" IS NULL OR \"ResponsibleId\" <> \"DeputyId\"");
                    table.ForeignKey(
                        name: "FK_responsibilities_users_DeputyId",
                        column: x => x.DeputyId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_responsibilities_users_ResponsibleId",
                        column: x => x.ResponsibleId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "responsibility_settings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false),
                    GlobalResponsibleId = table.Column<Guid>(type: "uuid", nullable: true),
                    GlobalDeputyId = table.Column<Guid>(type: "uuid", nullable: true),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedBy = table.Column<Guid>(type: "uuid", nullable: true),
                    UpdatedByName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_responsibility_settings", x => x.Id);
                    table.CheckConstraint("CK_responsibility_settings_one_row", "\"Id\" = 1");
                    table.CheckConstraint("CK_responsibility_settings_two_people", "\"GlobalResponsibleId\" IS NULL OR \"GlobalDeputyId\" IS NULL OR \"GlobalResponsibleId\" <> \"GlobalDeputyId\"");
                    table.ForeignKey(
                        name: "FK_responsibility_settings_users_GlobalDeputyId",
                        column: x => x.GlobalDeputyId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_responsibility_settings_users_GlobalResponsibleId",
                        column: x => x.GlobalResponsibleId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "responsibility_history",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ResponsibilityId = table.Column<Guid>(type: "uuid", nullable: false),
                    At = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    ByName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Kind = table.Column<int>(type: "integer", nullable: false),
                    OldValue = table.Column<string>(type: "text", nullable: true),
                    NewValue = table.Column<string>(type: "text", nullable: true),
                    Detail = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_responsibility_history", x => x.Id);
                    table.ForeignKey(
                        name: "FK_responsibility_history_responsibilities_ResponsibilityId",
                        column: x => x.ResponsibilityId,
                        principalTable: "responsibilities",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "responsibility_links",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ResponsibilityId = table.Column<Guid>(type: "uuid", nullable: false),
                    PageId = table.Column<Guid>(type: "uuid", nullable: true),
                    LocationId = table.Column<Guid>(type: "uuid", nullable: true),
                    HostId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedByName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_responsibility_links", x => x.Id);
                    table.CheckConstraint("CK_responsibility_links_one_target", "num_nonnulls(\"PageId\", \"LocationId\", \"HostId\") = 1");
                    table.ForeignKey(
                        name: "FK_responsibility_links_hosts_HostId",
                        column: x => x.HostId,
                        principalTable: "hosts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_responsibility_links_kb_pages_PageId",
                        column: x => x.PageId,
                        principalTable: "kb_pages",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_responsibility_links_locations_LocationId",
                        column: x => x.LocationId,
                        principalTable: "locations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_responsibility_links_responsibilities_ResponsibilityId",
                        column: x => x.ResponsibilityId,
                        principalTable: "responsibilities",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.InsertData(
                table: "responsibility_settings",
                columns: new[] { "Id", "GlobalDeputyId", "GlobalResponsibleId", "UpdatedAt", "UpdatedBy", "UpdatedByName" },
                values: new object[] { 1, null, null, new DateTime(2026, 11, 23, 0, 0, 0, 0, DateTimeKind.Utc), null, null });

            migrationBuilder.CreateIndex(
                name: "IX_responsibilities_DeputyId",
                table: "responsibilities",
                column: "DeputyId");

            migrationBuilder.CreateIndex(
                name: "IX_responsibilities_NormalizedName",
                table: "responsibilities",
                column: "NormalizedName",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_responsibilities_ResponsibleId",
                table: "responsibilities",
                column: "ResponsibleId");

            migrationBuilder.CreateIndex(
                name: "IX_responsibility_history_ResponsibilityId_At",
                table: "responsibility_history",
                columns: new[] { "ResponsibilityId", "At" });

            migrationBuilder.CreateIndex(
                name: "IX_responsibility_links_HostId_ResponsibilityId",
                table: "responsibility_links",
                columns: new[] { "HostId", "ResponsibilityId" },
                unique: true,
                filter: "\"HostId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_responsibility_links_LocationId_ResponsibilityId",
                table: "responsibility_links",
                columns: new[] { "LocationId", "ResponsibilityId" },
                unique: true,
                filter: "\"LocationId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_responsibility_links_PageId_ResponsibilityId",
                table: "responsibility_links",
                columns: new[] { "PageId", "ResponsibilityId" },
                unique: true,
                filter: "\"PageId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_responsibility_links_ResponsibilityId",
                table: "responsibility_links",
                column: "ResponsibilityId");

            migrationBuilder.CreateIndex(
                name: "IX_responsibility_settings_GlobalDeputyId",
                table: "responsibility_settings",
                column: "GlobalDeputyId");

            migrationBuilder.CreateIndex(
                name: "IX_responsibility_settings_GlobalResponsibleId",
                table: "responsibility_settings",
                column: "GlobalResponsibleId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "responsibility_history");

            migrationBuilder.DropTable(
                name: "responsibility_links");

            migrationBuilder.DropTable(
                name: "responsibility_settings");

            migrationBuilder.DropTable(
                name: "responsibilities");
        }
    }
}
