using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PNMTD.Data.Migrations
{
    /// <inheritdoc />
    public partial class MikrotikUpdateMethod : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "UpdateCheckStorage",
                table: "mikrotik_routers",
                type: "boolean",
                nullable: false,
                // Existing routers keep today's check (docs/ai-operations-plan.md §12.2).
                defaultValue: true);

            migrationBuilder.AddColumn<short>(
                name: "UpdateMethod",
                table: "mikrotik_routers",
                type: "smallint",
                nullable: false,
                defaultValue: (short)0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "UpdateCheckStorage",
                table: "mikrotik_routers");

            migrationBuilder.DropColumn(
                name: "UpdateMethod",
                table: "mikrotik_routers");
        }
    }
}
