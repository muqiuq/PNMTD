using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PNMTD.Data.Migrations
{
    /// <inheritdoc />
    /// <summary>
    /// docs/board-messages-plan.md §9.4: a board's image elements use files as pages do, so that the cleanup keeps them; a use has one
    /// owner of four now. Every existing row keeps its owner: nothing to backfill.
    /// </summary>
    public partial class BoardImages : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_file_uses_one_owner",
                table: "file_uses");

            migrationBuilder.AddColumn<Guid>(
                name: "BoardId",
                table: "file_uses",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_file_uses_BoardId_FileId",
                table: "file_uses",
                columns: new[] { "BoardId", "FileId" },
                unique: true,
                filter: "\"BoardId\" IS NOT NULL");

            migrationBuilder.AddCheckConstraint(
                name: "CK_file_uses_one_owner",
                table: "file_uses",
                sql: "num_nonnulls(\"PageId\", \"LocationId\", \"ResponsibilityId\", \"BoardId\") = 1");

            migrationBuilder.AddForeignKey(
                name: "FK_file_uses_boards_BoardId",
                table: "file_uses",
                column: "BoardId",
                principalTable: "boards",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_file_uses_boards_BoardId",
                table: "file_uses");

            migrationBuilder.DropIndex(
                name: "IX_file_uses_BoardId_FileId",
                table: "file_uses");

            migrationBuilder.DropCheckConstraint(
                name: "CK_file_uses_one_owner",
                table: "file_uses");

            migrationBuilder.DropColumn(
                name: "BoardId",
                table: "file_uses");

            migrationBuilder.AddCheckConstraint(
                name: "CK_file_uses_one_owner",
                table: "file_uses",
                sql: "num_nonnulls(\"PageId\", \"LocationId\", \"ResponsibilityId\") = 1");
        }
    }
}
