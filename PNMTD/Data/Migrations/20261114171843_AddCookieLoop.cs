using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace PNMTD.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddCookieLoop : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "BriefHash",
                table: "cookie_runtime",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Failures",
                table: "cookie_runtime",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTime>(
                name: "PausedUntil",
                table: "cookie_runtime",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<List<string>>(
                name: "CookieNoteAreas",
                table: "cookie_cases",
                type: "text[]",
                nullable: false);

            migrationBuilder.CreateTable(
                name: "cookie_messages",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    At = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    From = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Text = table.Column<string>(type: "text", nullable: false),
                    Answered = table.Column<bool>(type: "boolean", nullable: false),
                    CaseId = table.Column<Guid>(type: "uuid", nullable: true),
                    RoundId = table.Column<long>(type: "bigint", nullable: true),
                    Areas = table.Column<List<string>>(type: "text[]", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_cookie_messages", x => x.Id);
                    table.ForeignKey(
                        name: "FK_cookie_messages_users_UserId",
                        column: x => x.UserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "cookie_pseudonyms",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Value = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    Original = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    Placeholder = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_cookie_pseudonyms", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "cookie_rounds",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    StartedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    EndedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Trigger = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    Outcome = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Cases = table.Column<int>(type: "integer", nullable: false),
                    Calls = table.Column<int>(type: "integer", nullable: false),
                    ToolCalls = table.Column<int>(type: "integer", nullable: false),
                    Cost = table.Column<decimal>(type: "numeric", nullable: false),
                    Error = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_cookie_rounds", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "cookie_trace",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    RoundId = table.Column<long>(type: "bigint", nullable: true),
                    CaseId = table.Column<Guid>(type: "uuid", nullable: true),
                    At = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Title = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    Body = table.Column<string>(type: "text", nullable: false),
                    Areas = table.Column<List<string>>(type: "text[]", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_cookie_trace", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_cookie_messages_Answered",
                table: "cookie_messages",
                column: "Answered",
                filter: "NOT \"Answered\"");

            migrationBuilder.CreateIndex(
                name: "IX_cookie_messages_UserId_Id",
                table: "cookie_messages",
                columns: new[] { "UserId", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_cookie_pseudonyms_Kind_Value",
                table: "cookie_pseudonyms",
                columns: new[] { "Kind", "Value" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_cookie_pseudonyms_Placeholder",
                table: "cookie_pseudonyms",
                column: "Placeholder",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_cookie_rounds_StartedAt",
                table: "cookie_rounds",
                column: "StartedAt");

            migrationBuilder.CreateIndex(
                name: "IX_cookie_trace_At",
                table: "cookie_trace",
                column: "At");

            migrationBuilder.CreateIndex(
                name: "IX_cookie_trace_CaseId_Id",
                table: "cookie_trace",
                columns: new[] { "CaseId", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_cookie_trace_RoundId",
                table: "cookie_trace",
                column: "RoundId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "cookie_messages");

            migrationBuilder.DropTable(
                name: "cookie_pseudonyms");

            migrationBuilder.DropTable(
                name: "cookie_rounds");

            migrationBuilder.DropTable(
                name: "cookie_trace");

            migrationBuilder.DropColumn(
                name: "BriefHash",
                table: "cookie_runtime");

            migrationBuilder.DropColumn(
                name: "Failures",
                table: "cookie_runtime");

            migrationBuilder.DropColumn(
                name: "PausedUntil",
                table: "cookie_runtime");

            migrationBuilder.DropColumn(
                name: "CookieNoteAreas",
                table: "cookie_cases");
        }
    }
}
