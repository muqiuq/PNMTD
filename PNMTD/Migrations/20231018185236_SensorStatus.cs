using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PNMTD.Migrations
{
    /// <inheritdoc />
    public partial class SensorStatus : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Status",
                table: "sensors",
                type: "TEXT",
                nullable: false,
                defaultValue: "");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Status",
                table: "sensors");
        }
    }
}
