using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PNMTD.Data.Migrations
{
    /// <inheritdoc />
    /// <summary>
    /// docs/boards-plan.md §7.5, §11.2: the problems Zabbix's poll saw, open and resolved, for the boards' history of problems. A new
    /// table, filled by the next poll; nothing to backfill.
    /// </summary>
    public partial class ZabbixProblemHistory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "zabbix_problem_history",
                columns: table => new
                {
                    EventId = table.Column<long>(type: "bigint", nullable: false),
                    TriggerId = table.Column<long>(type: "bigint", nullable: false),
                    Name = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                    Severity = table.Column<int>(type: "integer", nullable: false),
                    Suppressed = table.Column<bool>(type: "boolean", nullable: false),
                    HostIds = table.Column<List<long>>(type: "bigint[]", nullable: false),
                    HostNames = table.Column<List<string>>(type: "text[]", nullable: false),
                    StartedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ResolvedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ResolvedFrom = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    FirstSeenAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LastSeenAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_zabbix_problem_history", x => x.EventId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_zabbix_problem_history_ResolvedAt",
                table: "zabbix_problem_history",
                column: "ResolvedAt");

            migrationBuilder.CreateIndex(
                name: "IX_zabbix_problem_history_StartedAt",
                table: "zabbix_problem_history",
                column: "StartedAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "zabbix_problem_history");
        }
    }
}
