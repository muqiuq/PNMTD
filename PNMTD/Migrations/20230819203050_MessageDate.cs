using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PNMTD.Migrations
{
    /// <inheritdoc />
    public partial class MessageDate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "MessageDate",
                table: "maillogs",
                type: "TEXT",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified));
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "MessageDate",
                table: "maillogs");
        }
    }
}
