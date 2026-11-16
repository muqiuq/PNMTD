using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PNMTD.Data.Migrations
{
    /// <inheritdoc />
    public partial class CookiePowers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "Empowered",
                table: "cookie_messages",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "OrderedBy",
                table: "cookie_decisions",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "VerdictConfidence",
                table: "cookie_decisions",
                type: "double precision",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Empowered",
                table: "cookie_messages");

            migrationBuilder.DropColumn(
                name: "OrderedBy",
                table: "cookie_decisions");

            migrationBuilder.DropColumn(
                name: "VerdictConfidence",
                table: "cookie_decisions");
        }
    }
}
