using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace PNMTD.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddCookie : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "cookie_cases",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Key = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Open = table.Column<bool>(type: "boolean", nullable: false),
                    Title = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    HostId = table.Column<Guid>(type: "uuid", nullable: true),
                    LocationId = table.Column<Guid>(type: "uuid", nullable: true),
                    ZabbixHostId = table.Column<long>(type: "bigint", nullable: true),
                    IssueId = table.Column<int>(type: "integer", nullable: true),
                    ProjectId = table.Column<int>(type: "integer", nullable: true),
                    Importance = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    OpenedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ClosedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ChangedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ProgressAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Members = table.Column<string>(type: "text", nullable: false),
                    NotesHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    AuthorId = table.Column<int>(type: "integer", nullable: true),
                    AuthorName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    AuthorIsStaff = table.Column<bool>(type: "boolean", nullable: false),
                    AssigneeId = table.Column<int>(type: "integer", nullable: true),
                    AssigneeName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    StatusId = table.Column<int>(type: "integer", nullable: true),
                    StatusName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    TicketCreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    TicketUpdatedOn = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    FirstAnswerAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastStaffUpdateAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    OwnIssueId = table.Column<int>(type: "integer", nullable: true),
                    CookieNote = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    CookieUrgency = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    CookieNoteAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Areas = table.Column<List<string>>(type: "text[]", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_cookie_cases", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "cookie_runtime",
                columns: table => new
                {
                    SingletonId = table.Column<int>(type: "integer", nullable: false),
                    Running = table.Column<bool>(type: "boolean", nullable: false),
                    StartedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    StartedBy = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    StoppedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    StoppedBy = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    State = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    StateReason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    LastRoundAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    NextRoundAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CodeStatus = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    CodeReason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    CodeCases = table.Column<List<Guid>>(type: "uuid[]", nullable: false),
                    CodeStatusAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CookieStatus = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    CookieReason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    CookieCases = table.Column<List<Guid>>(type: "uuid[]", nullable: false),
                    CookieStatusAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CaseBookAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    TicketsAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CaseBookError = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_cookie_runtime", x => x.SingletonId);
                });

            migrationBuilder.CreateTable(
                name: "cookie_settings",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Section = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Version = table.Column<int>(type: "integer", nullable: false),
                    Json = table.Column<string>(type: "text", nullable: false),
                    ChangedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ChangedById = table.Column<Guid>(type: "uuid", nullable: true),
                    ChangedByName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Note = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_cookie_settings", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "cookie_case_entries",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    CaseId = table.Column<Guid>(type: "uuid", nullable: false),
                    At = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Text = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    Actor = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    ActorName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_cookie_case_entries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_cookie_case_entries_cookie_cases_CaseId",
                        column: x => x.CaseId,
                        principalTable: "cookie_cases",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "cookie_duties",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    CaseId = table.Column<Guid>(type: "uuid", nullable: false),
                    Key = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    DueAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    State = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    DoneAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Outcome = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_cookie_duties", x => x.Id);
                    table.ForeignKey(
                        name: "FK_cookie_duties_cookie_cases_CaseId",
                        column: x => x.CaseId,
                        principalTable: "cookie_cases",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "cookie_outbox",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CaseId = table.Column<Guid>(type: "uuid", nullable: true),
                    DutyId = table.Column<long>(type: "bigint", nullable: true),
                    Channel = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: true),
                    Recipient = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    IssueId = table.Column<int>(type: "integer", nullable: true),
                    Subject = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    Body = table.Column<string>(type: "text", nullable: false),
                    State = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Detail = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    Source = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    SentAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    SentById = table.Column<Guid>(type: "uuid", nullable: true),
                    SentByName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_cookie_outbox", x => x.Id);
                    table.ForeignKey(
                        name: "FK_cookie_outbox_cookie_cases_CaseId",
                        column: x => x.CaseId,
                        principalTable: "cookie_cases",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "IX_cookie_case_entries_CaseId_At",
                table: "cookie_case_entries",
                columns: new[] { "CaseId", "At" });

            migrationBuilder.CreateIndex(
                name: "IX_cookie_cases_ClosedAt",
                table: "cookie_cases",
                column: "ClosedAt");

            migrationBuilder.CreateIndex(
                name: "IX_cookie_cases_HostId",
                table: "cookie_cases",
                column: "HostId");

            migrationBuilder.CreateIndex(
                name: "IX_cookie_cases_IssueId",
                table: "cookie_cases",
                column: "IssueId");

            migrationBuilder.CreateIndex(
                name: "IX_cookie_cases_Key",
                table: "cookie_cases",
                column: "Key",
                unique: true,
                filter: "\"Open\"");

            migrationBuilder.CreateIndex(
                name: "IX_cookie_cases_Open_Kind",
                table: "cookie_cases",
                columns: new[] { "Open", "Kind" });

            migrationBuilder.CreateIndex(
                name: "IX_cookie_duties_CaseId_Key",
                table: "cookie_duties",
                columns: new[] { "CaseId", "Key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_cookie_duties_State_DueAt",
                table: "cookie_duties",
                columns: new[] { "State", "DueAt" });

            migrationBuilder.CreateIndex(
                name: "IX_cookie_outbox_CaseId",
                table: "cookie_outbox",
                column: "CaseId");

            migrationBuilder.CreateIndex(
                name: "IX_cookie_outbox_Channel_UserId_CreatedAt",
                table: "cookie_outbox",
                columns: new[] { "Channel", "UserId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_cookie_outbox_CreatedAt",
                table: "cookie_outbox",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_cookie_outbox_IssueId_CreatedAt",
                table: "cookie_outbox",
                columns: new[] { "IssueId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_cookie_settings_Section_Version",
                table: "cookie_settings",
                columns: new[] { "Section", "Version" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "cookie_case_entries");

            migrationBuilder.DropTable(
                name: "cookie_duties");

            migrationBuilder.DropTable(
                name: "cookie_outbox");

            migrationBuilder.DropTable(
                name: "cookie_runtime");

            migrationBuilder.DropTable(
                name: "cookie_settings");

            migrationBuilder.DropTable(
                name: "cookie_cases");
        }
    }
}
