using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace PNMTD.Data.Migrations
{
    /// <inheritdoc />
    public partial class OutboundLogs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "outbound_notifications",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    StartedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    DurationMs = table.Column<int>(type: "integer", nullable: false),
                    Channel = table.Column<short>(type: "smallint", nullable: false),
                    Method = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Target = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    Recipient = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    Summary = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    Purpose = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    AskedBy = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Outcome = table.Column<short>(type: "smallint", nullable: false),
                    StatusCode = table.Column<int>(type: "integer", nullable: true),
                    Error = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    RequestHeaders = table.Column<string>(type: "jsonb", nullable: false),
                    ResponseHeaders = table.Column<string>(type: "jsonb", nullable: false),
                    RequestBody = table.Column<byte[]>(type: "bytea", nullable: true),
                    ResponseBody = table.Column<byte[]>(type: "bytea", nullable: true),
                    RequestBytes = table.Column<long>(type: "bigint", nullable: false),
                    ResponseBytes = table.Column<long>(type: "bigint", nullable: false),
                    Truncated = table.Column<bool>(type: "boolean", nullable: false),
                    Transcript = table.Column<byte[]>(type: "bytea", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_outbound_notifications", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "redmine_calls",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    StartedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    DurationMs = table.Column<int>(type: "integer", nullable: false),
                    QueuedMs = table.Column<int>(type: "integer", nullable: false),
                    Kind = table.Column<short>(type: "smallint", nullable: false),
                    Method = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Target = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    Summary = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    Purpose = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    AskedBy = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    JobId = table.Column<Guid>(type: "uuid", nullable: true),
                    Lane = table.Column<short>(type: "smallint", nullable: false),
                    Outcome = table.Column<short>(type: "smallint", nullable: false),
                    StatusCode = table.Column<int>(type: "integer", nullable: true),
                    Error = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    RequestHeaders = table.Column<string>(type: "jsonb", nullable: false),
                    ResponseHeaders = table.Column<string>(type: "jsonb", nullable: false),
                    RequestBody = table.Column<byte[]>(type: "bytea", nullable: true),
                    ResponseBody = table.Column<byte[]>(type: "bytea", nullable: true),
                    RequestBytes = table.Column<long>(type: "bigint", nullable: false),
                    ResponseBytes = table.Column<long>(type: "bigint", nullable: false),
                    Truncated = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_redmine_calls", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_outbound_notifications_Channel_Id",
                table: "outbound_notifications",
                columns: new[] { "Channel", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_outbound_notifications_Outcome_Id",
                table: "outbound_notifications",
                columns: new[] { "Outcome", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_redmine_calls_JobId",
                table: "redmine_calls",
                column: "JobId");

            migrationBuilder.CreateIndex(
                name: "IX_redmine_calls_Outcome_Id",
                table: "redmine_calls",
                columns: new[] { "Outcome", "Id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "outbound_notifications");

            migrationBuilder.DropTable(
                name: "redmine_calls");
        }
    }
}
