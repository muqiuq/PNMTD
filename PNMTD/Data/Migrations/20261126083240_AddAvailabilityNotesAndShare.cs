using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PNMTD.Data.Migrations
{
    /// <summary>
    /// The availability plan's notes on days, and each person's share as the day's responsible, 100 (a full share) for everyone
    /// already there (docs/availability-extensions-plan.md §6, §8).
    /// </summary>
    public partial class AddAvailabilityNotesAndShare : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ResponsibleShare",
                table: "users",
                type: "integer",
                nullable: false,
                defaultValue: 100);

            migrationBuilder.CreateTable(
                name: "availability_notes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Date = table.Column<DateOnly>(type: "date", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    AuthorName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Text = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ChangedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_availability_notes", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_availability_notes_Date",
                table: "availability_notes",
                column: "Date");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "availability_notes");

            migrationBuilder.DropColumn(
                name: "ResponsibleShare",
                table: "users");
        }
    }
}
