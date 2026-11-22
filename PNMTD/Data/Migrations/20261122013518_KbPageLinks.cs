using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PNMTD.Data.Migrations
{
    /// <inheritdoc />
    /// <summary>
    /// docs/knowledge-base-extensions-plan.md §7: a page can link to another page, mutually, as two mirrored rows of kb_links (the check
    /// of one target becomes one of four, a page never links to itself, and deleting either page removes both rows); the revisions a
    /// page gets when the other page links to it are links-only, which no revision check counts.
    /// </summary>
    public partial class KbPageLinks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_kb_links_one_target",
                table: "kb_links");

            migrationBuilder.AddColumn<bool>(
                name: "LinksOnly",
                table: "kb_revisions",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<Guid>(
                name: "TargetPageId",
                table: "kb_links",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_kb_links_TargetPageId_PageId",
                table: "kb_links",
                columns: new[] { "TargetPageId", "PageId" },
                unique: true,
                filter: "\"TargetPageId\" IS NOT NULL");

            migrationBuilder.AddCheckConstraint(
                name: "CK_kb_links_not_itself",
                table: "kb_links",
                sql: "\"TargetPageId\" IS NULL OR \"TargetPageId\" <> \"PageId\"");

            migrationBuilder.AddCheckConstraint(
                name: "CK_kb_links_one_target",
                table: "kb_links",
                sql: "num_nonnulls(\"LocationId\", \"HostId\", \"SensorId\", \"TargetPageId\") = 1");

            migrationBuilder.AddForeignKey(
                name: "FK_kb_links_kb_pages_TargetPageId",
                table: "kb_links",
                column: "TargetPageId",
                principalTable: "kb_pages",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_kb_links_kb_pages_TargetPageId",
                table: "kb_links");

            migrationBuilder.DropIndex(
                name: "IX_kb_links_TargetPageId_PageId",
                table: "kb_links");

            migrationBuilder.DropCheckConstraint(
                name: "CK_kb_links_not_itself",
                table: "kb_links");

            migrationBuilder.DropCheckConstraint(
                name: "CK_kb_links_one_target",
                table: "kb_links");

            migrationBuilder.DropColumn(
                name: "LinksOnly",
                table: "kb_revisions");

            migrationBuilder.DropColumn(
                name: "TargetPageId",
                table: "kb_links");

            migrationBuilder.AddCheckConstraint(
                name: "CK_kb_links_one_target",
                table: "kb_links",
                sql: "num_nonnulls(\"LocationId\", \"HostId\", \"SensorId\") = 1");
        }
    }
}
