using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PNMTD.Data.Migrations
{
    /// <inheritdoc />
    /// <summary>
    /// docs/cookie-redmine-tab-plan.md §4.1: the Redmine user a person is, by id, picked on Admin → Cookie → Redmine; at most one person
    /// per Redmine user. Nobody is linked yet: until someone links them, the name finds the assignee, as before.
    /// </summary>
    public partial class RedmineLinks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "RedmineUserId",
                table: "users",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_users_RedmineUserId",
                table: "users",
                column: "RedmineUserId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_users_RedmineUserId",
                table: "users");

            migrationBuilder.DropColumn(
                name: "RedmineUserId",
                table: "users");
        }
    }
}
