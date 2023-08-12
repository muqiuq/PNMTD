using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PNMTD.Migrations
{
    /// <inheritdoc />
    public partial class SensorPocoSource : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Source",
                table: "sensors",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Source",
                table: "sensors");
        }
    }
}
