using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;
using NpgsqlTypes;

#nullable disable

namespace PNMTD.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddKnowledgeBase : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "kb_pages",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Number = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Title = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    Summary = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    Content = table.Column<string>(type: "text", nullable: false),
                    PlainText = table.Column<string>(type: "text", nullable: false),
                    TagsText = table.Column<string>(type: "text", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    Sensitive = table.Column<bool>(type: "boolean", nullable: false),
                    Revision = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedByName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedBy = table.Column<Guid>(type: "uuid", nullable: true),
                    UpdatedByName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    ArchivedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ArchivedBy = table.Column<Guid>(type: "uuid", nullable: true),
                    ArchivedByName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    Views = table.Column<int>(type: "integer", nullable: false),
                    SearchVector = table.Column<NpgsqlTsVector>(type: "tsvector", nullable: false, computedColumnSql: "setweight(to_tsvector('simple'::regconfig, coalesce(\"Title\", '') || ' ' || coalesce(\"TagsText\", '')), 'A') ||\nsetweight(to_tsvector('simple'::regconfig, coalesce(\"Summary\", '')), 'B') ||\nsetweight(to_tsvector('simple'::regconfig, left(coalesce(\"PlainText\", ''), 200000)), 'C')", stored: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_kb_pages", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "kb_tags",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    NormalizedName = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_kb_tags", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "kb_ai_questions",
                columns: table => new
                {
                    PageId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    AskedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_kb_ai_questions", x => new { x.PageId, x.UserId });
                    table.ForeignKey(
                        name: "FK_kb_ai_questions_kb_pages_PageId",
                        column: x => x.PageId,
                        principalTable: "kb_pages",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "kb_links",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    PageId = table.Column<Guid>(type: "uuid", nullable: false),
                    LocationId = table.Column<Guid>(type: "uuid", nullable: true),
                    HostId = table.Column<Guid>(type: "uuid", nullable: true),
                    SensorId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_kb_links", x => x.Id);
                    table.CheckConstraint("CK_kb_links_one_target", "num_nonnulls(\"LocationId\", \"HostId\", \"SensorId\") = 1");
                    table.ForeignKey(
                        name: "FK_kb_links_hosts_HostId",
                        column: x => x.HostId,
                        principalTable: "hosts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_kb_links_kb_pages_PageId",
                        column: x => x.PageId,
                        principalTable: "kb_pages",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_kb_links_locations_LocationId",
                        column: x => x.LocationId,
                        principalTable: "locations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_kb_links_sensors_SensorId",
                        column: x => x.SensorId,
                        principalTable: "sensors",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "kb_page_refs",
                columns: table => new
                {
                    FromPageId = table.Column<Guid>(type: "uuid", nullable: false),
                    ToNumber = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_kb_page_refs", x => new { x.FromPageId, x.ToNumber });
                    table.ForeignKey(
                        name: "FK_kb_page_refs_kb_pages_FromPageId",
                        column: x => x.FromPageId,
                        principalTable: "kb_pages",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "kb_page_views",
                columns: table => new
                {
                    PageId = table.Column<Guid>(type: "uuid", nullable: false),
                    Day = table.Column<DateOnly>(type: "date", nullable: false),
                    Views = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_kb_page_views", x => new { x.PageId, x.Day });
                    table.ForeignKey(
                        name: "FK_kb_page_views_kb_pages_PageId",
                        column: x => x.PageId,
                        principalTable: "kb_pages",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "kb_revisions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    PageId = table.Column<Guid>(type: "uuid", nullable: false),
                    Number = table.Column<int>(type: "integer", nullable: false),
                    At = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    ByName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Title = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    Summary = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    Content = table.Column<string>(type: "text", nullable: false),
                    Tags = table.Column<List<string>>(type: "text[]", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    Sensitive = table.Column<bool>(type: "boolean", nullable: false),
                    Archived = table.Column<bool>(type: "boolean", nullable: false),
                    Links = table.Column<string>(type: "jsonb", nullable: true),
                    Note = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    Added = table.Column<int>(type: "integer", nullable: false),
                    Removed = table.Column<int>(type: "integer", nullable: false),
                    Changes = table.Column<string>(type: "jsonb", nullable: false),
                    Declaration = table.Column<string>(type: "jsonb", nullable: true),
                    IsEdit = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_kb_revisions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_kb_revisions_kb_pages_PageId",
                        column: x => x.PageId,
                        principalTable: "kb_pages",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "kb_page_tags",
                columns: table => new
                {
                    PageId = table.Column<Guid>(type: "uuid", nullable: false),
                    TagId = table.Column<Guid>(type: "uuid", nullable: false),
                    Source = table.Column<int>(type: "integer", nullable: false),
                    AddedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    AddedBy = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_kb_page_tags", x => new { x.PageId, x.TagId });
                    table.ForeignKey(
                        name: "FK_kb_page_tags_kb_pages_PageId",
                        column: x => x.PageId,
                        principalTable: "kb_pages",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_kb_page_tags_kb_tags_TagId",
                        column: x => x.TagId,
                        principalTable: "kb_tags",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_kb_ai_questions_AskedAt",
                table: "kb_ai_questions",
                column: "AskedAt");

            migrationBuilder.CreateIndex(
                name: "IX_kb_links_HostId_PageId",
                table: "kb_links",
                columns: new[] { "HostId", "PageId" },
                unique: true,
                filter: "\"HostId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_kb_links_LocationId_PageId",
                table: "kb_links",
                columns: new[] { "LocationId", "PageId" },
                unique: true,
                filter: "\"LocationId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_kb_links_PageId",
                table: "kb_links",
                column: "PageId");

            migrationBuilder.CreateIndex(
                name: "IX_kb_links_SensorId_PageId",
                table: "kb_links",
                columns: new[] { "SensorId", "PageId" },
                unique: true,
                filter: "\"SensorId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_kb_page_refs_ToNumber",
                table: "kb_page_refs",
                column: "ToNumber");

            migrationBuilder.CreateIndex(
                name: "IX_kb_page_tags_TagId",
                table: "kb_page_tags",
                column: "TagId");

            migrationBuilder.CreateIndex(
                name: "IX_kb_page_views_Day",
                table: "kb_page_views",
                column: "Day");

            migrationBuilder.CreateIndex(
                name: "IX_kb_pages_CreatedAt",
                table: "kb_pages",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_kb_pages_Number",
                table: "kb_pages",
                column: "Number",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_kb_pages_SearchVector",
                table: "kb_pages",
                column: "SearchVector")
                .Annotation("Npgsql:IndexMethod", "GIN");

            migrationBuilder.CreateIndex(
                name: "IX_kb_pages_UpdatedAt",
                table: "kb_pages",
                column: "UpdatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_kb_pages_Views",
                table: "kb_pages",
                column: "Views");

            migrationBuilder.CreateIndex(
                name: "IX_kb_revisions_At",
                table: "kb_revisions",
                column: "At");

            migrationBuilder.CreateIndex(
                name: "IX_kb_revisions_PageId_Number",
                table: "kb_revisions",
                columns: new[] { "PageId", "Number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_kb_tags_NormalizedName",
                table: "kb_tags",
                column: "NormalizedName",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "kb_ai_questions");

            migrationBuilder.DropTable(
                name: "kb_links");

            migrationBuilder.DropTable(
                name: "kb_page_refs");

            migrationBuilder.DropTable(
                name: "kb_page_tags");

            migrationBuilder.DropTable(
                name: "kb_page_views");

            migrationBuilder.DropTable(
                name: "kb_revisions");

            migrationBuilder.DropTable(
                name: "kb_tags");

            migrationBuilder.DropTable(
                name: "kb_pages");
        }
    }
}
