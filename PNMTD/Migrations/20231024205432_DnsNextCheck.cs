using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PNMTD.Migrations
{
    /// <inheritdoc />
    public partial class DnsNextCheck : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "ForceUpdate",
                table: "dnszones",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "Interval",
                table: "dnszones",
                type: "INTEGER",
                nullable: false,
                defaultValue: 600);

            migrationBuilder.AddColumn<DateTime>(
                name: "NextCheck",
                table: "dnszones",
                type: "TEXT",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified));
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ForceUpdate",
                table: "dnszones");

            migrationBuilder.DropColumn(
                name: "Interval",
                table: "dnszones");

            migrationBuilder.DropColumn(
                name: "NextCheck",
                table: "dnszones");
        }
    }
}
