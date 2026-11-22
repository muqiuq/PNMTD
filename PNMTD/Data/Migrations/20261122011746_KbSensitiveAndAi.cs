using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PNMTD.Data.Migrations
{
    /// <inheritdoc />
    /// <summary>
    /// docs/knowledge-base-extensions-plan.md §5: <i>Written mostly with AI</i> on the page and in every revision; false for the pages
    /// and revisions that exist.
    /// </summary>
    public partial class KbSensitiveAndAi : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "WrittenWithAi",
                table: "kb_revisions",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "WrittenWithAi",
                table: "kb_pages",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "WrittenWithAi",
                table: "kb_revisions");

            migrationBuilder.DropColumn(
                name: "WrittenWithAi",
                table: "kb_pages");
        }
    }
}
