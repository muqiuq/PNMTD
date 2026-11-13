using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PNMTD.Data.Migrations
{
    /// <inheritdoc />
    public partial class DecisionsAndMailAi : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<short>(
                name: "AiState",
                table: "maillogs",
                type: "smallint",
                nullable: false,
                defaultValue: (short)0);

            migrationBuilder.AddColumn<Guid>(
                name: "DecisionId",
                table: "maillogs",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<short>(
                name: "Disposition",
                table: "maillogs",
                type: "smallint",
                nullable: false,
                defaultValue: (short)0);

            migrationBuilder.AddColumn<short>(
                name: "Action",
                table: "mailinputrules",
                type: "smallint",
                nullable: false,
                defaultValue: (short)0);

            migrationBuilder.AddColumn<bool>(
                name: "AskAiWhenUnsure",
                table: "mailinputrules",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "VerifyAi",
                table: "mailinputrules",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.CreateTable(
                name: "decisions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    State = table.Column<short>(type: "smallint", nullable: false),
                    RequiredRole = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Title = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    Summary = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    Payload = table.Column<string>(type: "jsonb", nullable: false),
                    PayloadVersion = table.Column<int>(type: "integer", nullable: false),
                    SourceType = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    SourceId = table.Column<Guid>(type: "uuid", nullable: true),
                    DedupeKey = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    Occurrences = table.Column<int>(type: "integer", nullable: false),
                    Subjects = table.Column<List<Guid>>(type: "uuid[]", nullable: false),
                    Disputed = table.Column<bool>(type: "boolean", nullable: false),
                    Priority = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LastSeenAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ExpiresAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    DecidedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    DecidedBy = table.Column<Guid>(type: "uuid", nullable: true),
                    DecidedByName = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    Choice = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    Edits = table.Column<string>(type: "jsonb", nullable: true),
                    Result = table.Column<string>(type: "jsonb", nullable: true),
                    Error = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    AiWorkId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_decisions", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_decisions_DedupeKey",
                table: "decisions",
                column: "DedupeKey",
                unique: true,
                filter: "\"State\" = 0");

            migrationBuilder.CreateIndex(
                name: "IX_decisions_State_Kind",
                table: "decisions",
                columns: new[] { "State", "Kind" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "decisions");

            migrationBuilder.DropColumn(
                name: "AiState",
                table: "maillogs");

            migrationBuilder.DropColumn(
                name: "DecisionId",
                table: "maillogs");

            migrationBuilder.DropColumn(
                name: "Disposition",
                table: "maillogs");

            migrationBuilder.DropColumn(
                name: "Action",
                table: "mailinputrules");

            migrationBuilder.DropColumn(
                name: "AskAiWhenUnsure",
                table: "mailinputrules");

            migrationBuilder.DropColumn(
                name: "VerifyAi",
                table: "mailinputrules");
        }
    }
}
