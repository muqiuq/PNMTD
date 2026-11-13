using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace PNMTD.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddMikrotikJobs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "mikrotik_jobs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    BatchId = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<short>(type: "smallint", nullable: false),
                    SensorId = table.Column<Guid>(type: "uuid", nullable: true),
                    RouterName = table.Column<string>(type: "text", nullable: false),
                    State = table.Column<short>(type: "smallint", nullable: false),
                    Step = table.Column<short>(type: "smallint", nullable: false),
                    RequestedBy = table.Column<Guid>(type: "uuid", nullable: true),
                    RequestedByName = table.Column<string>(type: "text", nullable: true),
                    AdminUsername = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    StartedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    FinishedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    FromVersion = table.Column<string>(type: "text", nullable: true),
                    ToVersion = table.Column<string>(type: "text", nullable: true),
                    Channel = table.Column<string>(type: "text", nullable: true),
                    Options = table.Column<string>(type: "jsonb", nullable: true),
                    Error = table.Column<string>(type: "text", nullable: true),
                    Command = table.Column<string>(type: "text", nullable: true),
                    RunAs = table.Column<string>(type: "text", nullable: true),
                    TimeoutSeconds = table.Column<int>(type: "integer", nullable: true),
                    ExitStatus = table.Column<int>(type: "integer", nullable: true),
                    Output = table.Column<string>(type: "text", nullable: true),
                    OutputTruncated = table.Column<bool>(type: "boolean", nullable: false),
                    BackupBeforeId = table.Column<Guid>(type: "uuid", nullable: true),
                    BackupAfterId = table.Column<Guid>(type: "uuid", nullable: true),
                    RedactedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    RedactedBy = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_mikrotik_jobs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_mikrotik_jobs_sensors_SensorId",
                        column: x => x.SensorId,
                        principalTable: "sensors",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "mikrotik_job_log",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    JobId = table.Column<Guid>(type: "uuid", nullable: false),
                    At = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Step = table.Column<short>(type: "smallint", nullable: false),
                    Outcome = table.Column<short>(type: "smallint", nullable: false),
                    Message = table.Column<string>(type: "text", nullable: false),
                    Output = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_mikrotik_job_log", x => x.Id);
                    table.ForeignKey(
                        name: "FK_mikrotik_job_log_mikrotik_jobs_JobId",
                        column: x => x.JobId,
                        principalTable: "mikrotik_jobs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_mikrotik_job_log_JobId_At",
                table: "mikrotik_job_log",
                columns: new[] { "JobId", "At" });

            migrationBuilder.CreateIndex(
                name: "IX_mikrotik_jobs_BatchId",
                table: "mikrotik_jobs",
                column: "BatchId");

            migrationBuilder.CreateIndex(
                name: "IX_mikrotik_jobs_SensorId_CreatedAt",
                table: "mikrotik_jobs",
                columns: new[] { "SensorId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_mikrotik_jobs_State_CreatedAt",
                table: "mikrotik_jobs",
                columns: new[] { "State", "CreatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "mikrotik_job_log");

            migrationBuilder.DropTable(
                name: "mikrotik_jobs");
        }
    }
}
