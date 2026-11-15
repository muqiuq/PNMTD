using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace PNMTD.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddMcp : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "LastSeenAt",
                table: "users",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Via",
                table: "kb_revisions",
                type: "character varying(16)",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "McpTokenId",
                table: "cookie_messages",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "ReadOnly",
                table: "cookie_messages",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "RefusedWhy",
                table: "cookie_messages",
                type: "character varying(300)",
                maxLength: 300,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Source",
                table: "cookie_messages",
                type: "character varying(8)",
                maxLength: 8,
                nullable: false,
                defaultValue: "ui");

            migrationBuilder.CreateTable(
                name: "mcp_log",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    At = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CorrelationId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    TokenId = table.Column<Guid>(type: "uuid", nullable: true),
                    Kind = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: true),
                    UserId = table.Column<Guid>(type: "uuid", nullable: true),
                    Username = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Ip = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    UserAgent = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    Client = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    Method = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Tool = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    ToolKind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    Arguments = table.Column<string>(type: "jsonb", nullable: true),
                    Outcome = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    ErrorCode = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    Error = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    Summary = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    Targets = table.Column<string>(type: "jsonb", nullable: true),
                    Result = table.Column<byte[]>(type: "bytea", nullable: true),
                    Truncated = table.Column<bool>(type: "boolean", nullable: false),
                    Guard = table.Column<string>(type: "jsonb", nullable: true),
                    CookieMessageId = table.Column<long>(type: "bigint", nullable: true),
                    Strike = table.Column<bool>(type: "boolean", nullable: false),
                    DurationMs = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_mcp_log", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "mcp_tokens",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    Hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    Hint = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: true),
                    IssuedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    OnSince = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    OnUntil = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    OnStamp = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    DeleteUntil = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    DeleteCount = table.Column<int>(type: "integer", nullable: false),
                    LastUsedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastUsedFrom = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    LastClient = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    Strikes = table.Column<List<DateTime>>(type: "timestamp with time zone[]", nullable: false),
                    BlockedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    EndedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    EndedWhy = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    EndedNote = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_mcp_tokens", x => x.Id);
                    table.ForeignKey(
                        name: "FK_mcp_tokens_users_UserId",
                        column: x => x.UserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_mcp_log_At",
                table: "mcp_log",
                column: "At");

            migrationBuilder.CreateIndex(
                name: "IX_mcp_log_CorrelationId",
                table: "mcp_log",
                column: "CorrelationId");

            migrationBuilder.CreateIndex(
                name: "IX_mcp_log_TokenId_Id",
                table: "mcp_log",
                columns: new[] { "TokenId", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_mcp_log_Tool_Id",
                table: "mcp_log",
                columns: new[] { "Tool", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_mcp_log_UserId_Id",
                table: "mcp_log",
                columns: new[] { "UserId", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_mcp_tokens_Hash",
                table: "mcp_tokens",
                column: "Hash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_mcp_tokens_UserId_Kind",
                table: "mcp_tokens",
                columns: new[] { "UserId", "Kind" },
                unique: true);

            // Every user has both tokens from the start (docs/mcp-plan.md §4.1), without a value until they first switch one on.
            migrationBuilder.Sql("""
                INSERT INTO mcp_tokens ("Id", "UserId", "Kind", "DeleteCount", "Strikes")
                SELECT gen_random_uuid(), u."Id", k.kind, 0, '{}'
                FROM users u CROSS JOIN (VALUES ('Read'), ('Write')) AS k(kind);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "mcp_log");

            migrationBuilder.DropTable(
                name: "mcp_tokens");

            migrationBuilder.DropColumn(
                name: "LastSeenAt",
                table: "users");

            migrationBuilder.DropColumn(
                name: "Via",
                table: "kb_revisions");

            migrationBuilder.DropColumn(
                name: "McpTokenId",
                table: "cookie_messages");

            migrationBuilder.DropColumn(
                name: "ReadOnly",
                table: "cookie_messages");

            migrationBuilder.DropColumn(
                name: "RefusedWhy",
                table: "cookie_messages");

            migrationBuilder.DropColumn(
                name: "Source",
                table: "cookie_messages");
        }
    }
}
