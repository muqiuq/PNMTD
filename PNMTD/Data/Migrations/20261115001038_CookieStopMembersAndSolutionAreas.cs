using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PNMTD.Data.Migrations
{
    /// <inheritdoc />
    public partial class CookieStopMembersAndSolutionAreas : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Empty for the cases there are already (docs/cookie-plan.md §21.8).
            migrationBuilder.AddColumn<List<string>>(
                name: "SolutionAreas",
                table: "cookie_cases",
                type: "text[]",
                nullable: false,
                defaultValueSql: "'{}'::text[]");

            migrationBuilder.AddColumn<List<string>>(
                name: "StoppedMembers",
                table: "cookie_cases",
                type: "text[]",
                nullable: false,
                defaultValueSql: "'{}'::text[]");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SolutionAreas",
                table: "cookie_cases");

            migrationBuilder.DropColumn(
                name: "StoppedMembers",
                table: "cookie_cases");
        }
    }
}
