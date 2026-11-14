using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace PNMTD.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddCookieActing : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "InstructionId",
                table: "cookie_messages",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "To",
                table: "cookie_messages",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "cookie");
            // A reply was to its person (docs/cookie-plan.md §21.2).
            migrationBuilder.Sql("""UPDATE cookie_messages SET "To" = 'person' WHERE "From" <> 'person'""");

            migrationBuilder.AddColumn<DateTime>(
                name: "BigBrotherAskedAt",
                table: "cookie_cases",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "BigBrotherAskedBy",
                table: "cookie_cases",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Solution",
                table: "cookie_cases",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "SolutionAt",
                table: "cookie_cases",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SolutionModel",
                table: "cookie_cases",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "cookie_decisions",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    At = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CaseId = table.Column<Guid>(type: "uuid", nullable: true),
                    RoundId = table.Column<long>(type: "bigint", nullable: true),
                    Kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Title = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    Target = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    Proposal = table.Column<string>(type: "text", nullable: false),
                    Confidence = table.Column<double>(type: "double precision", nullable: true),
                    Verdict = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    VerdictReason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    Outcome = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Detail = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    InboxDecisionId = table.Column<Guid>(type: "uuid", nullable: true),
                    OutboxId = table.Column<long>(type: "bigint", nullable: true),
                    Model = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    Rating = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: true),
                    RatingComment = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    RatedById = table.Column<Guid>(type: "uuid", nullable: true),
                    RatedByName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    RatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    RatingRead = table.Column<bool>(type: "boolean", nullable: false),
                    Areas = table.Column<List<string>>(type: "text[]", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_cookie_decisions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_cookie_decisions_cookie_cases_CaseId",
                        column: x => x.CaseId,
                        principalTable: "cookie_cases",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "cookie_instructions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Scope = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Value = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    Label = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    Text = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    Until = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Confirmed = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedById = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedByName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Source = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    EndedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    EndedByName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_cookie_instructions", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_cookie_decisions_At",
                table: "cookie_decisions",
                column: "At");

            migrationBuilder.CreateIndex(
                name: "IX_cookie_decisions_CaseId_Kind_At",
                table: "cookie_decisions",
                columns: new[] { "CaseId", "Kind", "At" });

            migrationBuilder.CreateIndex(
                name: "IX_cookie_decisions_InboxDecisionId",
                table: "cookie_decisions",
                column: "InboxDecisionId");

            migrationBuilder.CreateIndex(
                name: "IX_cookie_instructions_Confirmed_EndedAt_Until",
                table: "cookie_instructions",
                columns: new[] { "Confirmed", "EndedAt", "Until" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "cookie_decisions");

            migrationBuilder.DropTable(
                name: "cookie_instructions");

            migrationBuilder.DropColumn(
                name: "InstructionId",
                table: "cookie_messages");

            migrationBuilder.DropColumn(
                name: "To",
                table: "cookie_messages");

            migrationBuilder.DropColumn(
                name: "BigBrotherAskedAt",
                table: "cookie_cases");

            migrationBuilder.DropColumn(
                name: "BigBrotherAskedBy",
                table: "cookie_cases");

            migrationBuilder.DropColumn(
                name: "Solution",
                table: "cookie_cases");

            migrationBuilder.DropColumn(
                name: "SolutionAt",
                table: "cookie_cases");

            migrationBuilder.DropColumn(
                name: "SolutionModel",
                table: "cookie_cases");
        }
    }
}
