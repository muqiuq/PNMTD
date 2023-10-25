using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PNMTD.Migrations
{
    /// <inheritdoc />
    public partial class SecretReadWriteTokens : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "SecretToken",
                table: "sensors",
                newName: "SecretWriteToken");

            migrationBuilder.AddColumn<string>(
                name: "SecretReadToken",
                table: "sensors",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SecretReadToken",
                table: "sensors");

            migrationBuilder.RenameColumn(
                name: "SecretWriteToken",
                table: "sensors",
                newName: "SecretToken");
        }
    }
}
