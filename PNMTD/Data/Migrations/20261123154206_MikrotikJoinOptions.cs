using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PNMTD.Data.Migrations
{
    /// <inheritdoc />
    public partial class MikrotikJoinOptions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AcknowledgedDifferences",
                table: "mikrotik_routers",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "ScriptFirewall",
                table: "mikrotik_routers",
                type: "boolean",
                nullable: false,
                // Existing routers keep the script as it always was (docs/mikrotik-join-options-plan.md §2.1).
                defaultValue: true);

            migrationBuilder.AddColumn<bool>(
                name: "ScriptRemovals",
                table: "mikrotik_routers",
                type: "boolean",
                nullable: false,
                // As above: removals on.
                defaultValue: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AcknowledgedDifferences",
                table: "mikrotik_routers");

            migrationBuilder.DropColumn(
                name: "ScriptFirewall",
                table: "mikrotik_routers");

            migrationBuilder.DropColumn(
                name: "ScriptRemovals",
                table: "mikrotik_routers");
        }
    }
}
