using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace PNMTD.Data.Migrations
{
    /// <inheritdoc />
    /// <summary>
    /// docs/cookie-redmine-tab-plan.md §6.2, §7: the newest journal the case book saw per ticket case (null on the cases there are: the
    /// next read starts at their newest journal, so nothing old is judged), and the away watch's comments.
    /// </summary>
    public partial class AwayWatch : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "LastJournalId",
                table: "cookie_cases",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "cookie_away_comments",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    CaseId = table.Column<Guid>(type: "uuid", nullable: false),
                    IssueId = table.Column<int>(type: "integer", nullable: false),
                    JournalId = table.Column<int>(type: "integer", nullable: false),
                    AssigneeUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    AuthorRedmineId = table.Column<int>(type: "integer", nullable: true),
                    AuthorName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    IsPrivate = table.Column<bool>(type: "boolean", nullable: false),
                    CommentAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Text = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    AwayUntil = table.Column<DateOnly>(type: "date", nullable: true),
                    State = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Importance = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: true),
                    Confidence = table.Column<double>(type: "double precision", nullable: true),
                    Reason = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    JudgedBy = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    JudgedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    DecisionId = table.Column<long>(type: "bigint", nullable: true),
                    NotifiedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Recipients = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    Detail = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_cookie_away_comments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_cookie_away_comments_cookie_cases_CaseId",
                        column: x => x.CaseId,
                        principalTable: "cookie_cases",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_cookie_away_comments_CaseId",
                table: "cookie_away_comments",
                column: "CaseId");

            migrationBuilder.CreateIndex(
                name: "IX_cookie_away_comments_IssueId_NotifiedAt",
                table: "cookie_away_comments",
                columns: new[] { "IssueId", "NotifiedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_cookie_away_comments_JournalId",
                table: "cookie_away_comments",
                column: "JournalId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_cookie_away_comments_State_CreatedAt",
                table: "cookie_away_comments",
                columns: new[] { "State", "CreatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "cookie_away_comments");

            migrationBuilder.DropColumn(
                name: "LastJournalId",
                table: "cookie_cases");
        }
    }
}
