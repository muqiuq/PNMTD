using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PNMTD.Data.Migrations
{
    /// <inheritdoc />
    /// <summary>
    /// docs/responsibilities-plan.md §10.4 (P2): a responsibility's description uses files as a page does, so file_uses gets a third
    /// owner (going with the responsibility) and its check of exactly one owner counts three.
    /// </summary>
    public partial class ResponsibilityFiles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_file_uses_one_owner",
                table: "file_uses");

            migrationBuilder.AddColumn<Guid>(
                name: "ResponsibilityId",
                table: "file_uses",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_file_uses_ResponsibilityId_FileId",
                table: "file_uses",
                columns: new[] { "ResponsibilityId", "FileId" },
                unique: true,
                filter: "\"ResponsibilityId\" IS NOT NULL");

            migrationBuilder.AddCheckConstraint(
                name: "CK_file_uses_one_owner",
                table: "file_uses",
                sql: "num_nonnulls(\"PageId\", \"LocationId\", \"ResponsibilityId\") = 1");

            migrationBuilder.AddForeignKey(
                name: "FK_file_uses_responsibilities_ResponsibilityId",
                table: "file_uses",
                column: "ResponsibilityId",
                principalTable: "responsibilities",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_file_uses_responsibilities_ResponsibilityId",
                table: "file_uses");

            migrationBuilder.DropIndex(
                name: "IX_file_uses_ResponsibilityId_FileId",
                table: "file_uses");

            migrationBuilder.DropCheckConstraint(
                name: "CK_file_uses_one_owner",
                table: "file_uses");

            migrationBuilder.DropColumn(
                name: "ResponsibilityId",
                table: "file_uses");

            migrationBuilder.AddCheckConstraint(
                name: "CK_file_uses_one_owner",
                table: "file_uses",
                sql: "num_nonnulls(\"PageId\", \"LocationId\") = 1");
        }
    }
}
