using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace PNMTD.Data.Migrations
{
    /// <inheritdoc />
    public partial class SystemSensorsAndUplinkChecks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<short>(
                name: "Importance",
                table: "sensors",
                type: "smallint",
                nullable: true);

            migrationBuilder.AddColumn<short>(
                name: "Importance",
                table: "hosts",
                type: "smallint",
                nullable: false,
                // Every existing host is Normal (docs/ai-operations-plan.md §11.1).
                defaultValue: (short)1);

            migrationBuilder.CreateTable(
                name: "uplink_checks",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    At = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Endpoint = table.Column<short>(type: "smallint", nullable: false),
                    EndpointHost = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    Outcome = table.Column<short>(type: "smallint", nullable: false),
                    HttpStatus = table.Column<int>(type: "integer", nullable: true),
                    LatencyMs = table.Column<int>(type: "integer", nullable: true),
                    Error = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_uplink_checks", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_sensors_Type_Parameters",
                table: "sensors",
                columns: new[] { "Type", "Parameters" },
                unique: true,
                filter: "\"Type\" = 90");

            migrationBuilder.CreateIndex(
                name: "IX_uplink_checks_At",
                table: "uplink_checks",
                column: "At");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "uplink_checks");

            migrationBuilder.DropIndex(
                name: "IX_sensors_Type_Parameters",
                table: "sensors");

            migrationBuilder.DropColumn(
                name: "Importance",
                table: "sensors");

            migrationBuilder.DropColumn(
                name: "Importance",
                table: "hosts");
        }
    }
}
