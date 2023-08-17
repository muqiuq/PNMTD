using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PNMTD.Migrations
{
    /// <inheritdoc />
    public partial class SensorIgnore : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "Ignore",
                table: "sensors",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Ignore",
                table: "sensors");
        }
    }
}
