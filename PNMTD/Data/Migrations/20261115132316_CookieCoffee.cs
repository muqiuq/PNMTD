using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PNMTD.Data.Migrations
{
    /// <inheritdoc />
    public partial class CookieCoffee : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CookieCoffee",
                table: "cookie_runtime",
                type: "character varying(300)",
                maxLength: 300,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SeraphCoffee",
                table: "cookie_runtime",
                type: "character varying(300)",
                maxLength: 300,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "SeraphCoffeeAt",
                table: "cookie_runtime",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CookieCoffee",
                table: "cookie_runtime");

            migrationBuilder.DropColumn(
                name: "SeraphCoffee",
                table: "cookie_runtime");

            migrationBuilder.DropColumn(
                name: "SeraphCoffeeAt",
                table: "cookie_runtime");
        }
    }
}
