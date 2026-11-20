using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace PNMTD.Data.Migrations
{
    /// <inheritdoc />
    public partial class Lounge : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // docs/lounge-plan.md §12. Both of a person's switches are on by default (D11), for the users there are too.
            migrationBuilder.AddColumn<bool>(
                name: "LoungeEmails",
                table: "users",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<bool>(
                name: "LoungeMentions",
                table: "users",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<long>(
                name: "LoungeMessageId",
                table: "cookie_cases",
                type: "bigint",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "lounge_memories",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Voice = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Text = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    Kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Keepsake = table.Column<bool>(type: "boolean", nullable: false),
                    Strength0 = table.Column<double>(type: "double precision", nullable: false),
                    RefreshedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Recalls = table.Column<int>(type: "integer", nullable: false),
                    SourceMessageId = table.Column<long>(type: "bigint", nullable: true),
                    People = table.Column<List<Guid>>(type: "uuid[]", nullable: false),
                    Areas = table.Column<List<string>>(type: "text[]", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_lounge_memories", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "lounge_opinions",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    At = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Conviction = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Voice = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Before = table.Column<double>(type: "double precision", nullable: false),
                    After = table.Column<double>(type: "double precision", nullable: false),
                    Why = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_lounge_opinions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "lounge_runtime",
                columns: table => new
                {
                    SingletonId = table.Column<int>(type: "integer", nullable: false),
                    Running = table.Column<bool>(type: "boolean", nullable: false),
                    StartedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    StartedBy = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    StoppedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    StoppedBy = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    State = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Speaking = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    StateReason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    NextAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    SceneId = table.Column<long>(type: "bigint", nullable: true),
                    LastNightAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    AwakeSince = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Positions = table.Column<string>(type: "text", nullable: false),
                    Failures = table.Column<int>(type: "integer", nullable: false),
                    PausedUntil = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    SleptAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    GroundHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    LastError = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    LastErrorAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_lounge_runtime", x => x.SingletonId);
                });

            migrationBuilder.CreateTable(
                name: "lounge_scenes",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    StartedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    EndedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Opener = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    SeedKey = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    Seed = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    Disagree = table.Column<bool>(type: "boolean", nullable: false),
                    PlannedLength = table.Column<int>(type: "integer", nullable: false),
                    Messages = table.Column<int>(type: "integer", nullable: false),
                    ToolCalls = table.Column<int>(type: "integer", nullable: false),
                    Cost = table.Column<decimal>(type: "numeric", nullable: false),
                    Outcome = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    GroundHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    MoveOn = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_lounge_scenes", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "lounge_messages",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    At = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    From = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: true),
                    Text = table.Column<string>(type: "text", nullable: false),
                    SceneId = table.Column<long>(type: "bigint", nullable: true),
                    ReplyToId = table.Column<long>(type: "bigint", nullable: true),
                    OnId = table.Column<long>(type: "bigint", nullable: true),
                    TakenById = table.Column<long>(type: "bigint", nullable: true),
                    Areas = table.Column<List<string>>(type: "text[]", nullable: false),
                    Model = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    Cost = table.Column<decimal>(type: "numeric", nullable: false),
                    ToolCalls = table.Column<int>(type: "integer", nullable: false),
                    CaseId = table.Column<Guid>(type: "uuid", nullable: true),
                    DecisionId = table.Column<long>(type: "bigint", nullable: true),
                    RefusedWhy = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    Answered = table.Column<bool>(type: "boolean", nullable: false),
                    DeletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    DeletedBy = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_lounge_messages", x => x.Id);
                    table.ForeignKey(
                        name: "FK_lounge_messages_cookie_cases_CaseId",
                        column: x => x.CaseId,
                        principalTable: "cookie_cases",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_lounge_messages_lounge_scenes_SceneId",
                        column: x => x.SceneId,
                        principalTable: "lounge_scenes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_lounge_messages_users_UserId",
                        column: x => x.UserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "lounge_steps",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    SceneId = table.Column<long>(type: "bigint", nullable: false),
                    MessageId = table.Column<long>(type: "bigint", nullable: true),
                    At = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Title = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    Body = table.Column<string>(type: "text", nullable: false),
                    Areas = table.Column<List<string>>(type: "text[]", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_lounge_steps", x => x.Id);
                    table.ForeignKey(
                        name: "FK_lounge_steps_lounge_scenes_SceneId",
                        column: x => x.SceneId,
                        principalTable: "lounge_scenes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_lounge_memories_Voice_Keepsake",
                table: "lounge_memories",
                columns: new[] { "Voice", "Keepsake" });

            migrationBuilder.CreateIndex(
                name: "IX_lounge_messages_At",
                table: "lounge_messages",
                column: "At");

            migrationBuilder.CreateIndex(
                name: "IX_lounge_messages_CaseId",
                table: "lounge_messages",
                column: "CaseId");

            migrationBuilder.CreateIndex(
                name: "IX_lounge_messages_Kind",
                table: "lounge_messages",
                column: "Kind",
                filter: "\"Kind\" = 'comment' AND \"TakenById\" IS NULL AND \"RefusedWhy\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_lounge_messages_OnId",
                table: "lounge_messages",
                column: "OnId");

            migrationBuilder.CreateIndex(
                name: "IX_lounge_messages_SceneId",
                table: "lounge_messages",
                column: "SceneId");

            migrationBuilder.CreateIndex(
                name: "IX_lounge_messages_UserId_At",
                table: "lounge_messages",
                columns: new[] { "UserId", "At" });

            migrationBuilder.CreateIndex(
                name: "IX_lounge_opinions_Conviction_At",
                table: "lounge_opinions",
                columns: new[] { "Conviction", "At" });

            migrationBuilder.CreateIndex(
                name: "IX_lounge_scenes_StartedAt",
                table: "lounge_scenes",
                column: "StartedAt");

            migrationBuilder.CreateIndex(
                name: "IX_lounge_steps_At",
                table: "lounge_steps",
                column: "At");

            migrationBuilder.CreateIndex(
                name: "IX_lounge_steps_SceneId_Id",
                table: "lounge_steps",
                columns: new[] { "SceneId", "Id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "lounge_memories");

            migrationBuilder.DropTable(
                name: "lounge_messages");

            migrationBuilder.DropTable(
                name: "lounge_opinions");

            migrationBuilder.DropTable(
                name: "lounge_runtime");

            migrationBuilder.DropTable(
                name: "lounge_steps");

            migrationBuilder.DropTable(
                name: "lounge_scenes");

            migrationBuilder.DropColumn(
                name: "LoungeEmails",
                table: "users");

            migrationBuilder.DropColumn(
                name: "LoungeMentions",
                table: "users");

            migrationBuilder.DropColumn(
                name: "LoungeMessageId",
                table: "cookie_cases");
        }
    }
}
