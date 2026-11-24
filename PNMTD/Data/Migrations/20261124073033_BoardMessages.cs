using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PNMTD.Data.Migrations
{
    /// <inheritdoc />
    /// <summary>
    /// docs/board-messages-plan.md §10: messages sent to boards, open until withdrawn or their time is up, kept 30 days after. A new
    /// table: nothing to backfill. The rights level Boards: Message needs no migration, as rights are stored by name.
    /// </summary>
    public partial class BoardMessages : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "board_messages",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    BoardIds = table.Column<Guid[]>(type: "uuid[]", nullable: false),
                    Kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Title = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Text = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    Seconds = table.Column<int>(type: "integer", nullable: false),
                    SentAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    EndsAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Source = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    SentById = table.Column<Guid>(type: "uuid", nullable: true),
                    SentByName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    ApprovedById = table.Column<Guid>(type: "uuid", nullable: true),
                    ApprovedByName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    CookieDecisionId = table.Column<long>(type: "bigint", nullable: true),
                    CookieCaseId = table.Column<Guid>(type: "uuid", nullable: true),
                    WithdrawnAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    WithdrawnById = table.Column<Guid>(type: "uuid", nullable: true),
                    WithdrawnByName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_board_messages", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_board_messages_EndsAt",
                table: "board_messages",
                column: "EndsAt");

            migrationBuilder.CreateIndex(
                name: "IX_board_messages_SentAt",
                table: "board_messages",
                column: "SentAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "board_messages");
        }
    }
}
