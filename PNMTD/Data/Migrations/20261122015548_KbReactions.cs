using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PNMTD.Data.Migrations
{
    /// <inheritdoc />
    /// <summary>
    /// docs/knowledge-base-extensions-plan.md §9: likes (once per person and page) and uses (an event, with an optional note), both going
    /// with their page, and the page's counters, 0 for the pages that exist.
    /// </summary>
    public partial class KbReactions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "LastUsedAt",
                table: "kb_pages",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Likes",
                table: "kb_pages",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "Uses",
                table: "kb_pages",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "kb_page_likes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    PageId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    At = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_kb_page_likes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_kb_page_likes_kb_pages_PageId",
                        column: x => x.PageId,
                        principalTable: "kb_pages",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "kb_page_uses",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    PageId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    At = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Note = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_kb_page_uses", x => x.Id);
                    table.ForeignKey(
                        name: "FK_kb_page_uses_kb_pages_PageId",
                        column: x => x.PageId,
                        principalTable: "kb_pages",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_kb_pages_Likes",
                table: "kb_pages",
                column: "Likes");

            migrationBuilder.CreateIndex(
                name: "IX_kb_pages_Uses",
                table: "kb_pages",
                column: "Uses");

            migrationBuilder.CreateIndex(
                name: "IX_kb_page_likes_At",
                table: "kb_page_likes",
                column: "At");

            migrationBuilder.CreateIndex(
                name: "IX_kb_page_likes_PageId_UserId",
                table: "kb_page_likes",
                columns: new[] { "PageId", "UserId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_kb_page_uses_PageId_At",
                table: "kb_page_uses",
                columns: new[] { "PageId", "At" });

            migrationBuilder.CreateIndex(
                name: "IX_kb_page_uses_PageId_UserId_At",
                table: "kb_page_uses",
                columns: new[] { "PageId", "UserId", "At" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "kb_page_likes");

            migrationBuilder.DropTable(
                name: "kb_page_uses");

            migrationBuilder.DropIndex(
                name: "IX_kb_pages_Likes",
                table: "kb_pages");

            migrationBuilder.DropIndex(
                name: "IX_kb_pages_Uses",
                table: "kb_pages");

            migrationBuilder.DropColumn(
                name: "LastUsedAt",
                table: "kb_pages");

            migrationBuilder.DropColumn(
                name: "Likes",
                table: "kb_pages");

            migrationBuilder.DropColumn(
                name: "Uses",
                table: "kb_pages");
        }
    }
}
