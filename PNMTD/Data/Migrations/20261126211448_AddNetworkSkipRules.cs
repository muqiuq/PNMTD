using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PNMTD.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddNetworkSkipRules : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "SkippedRuleId",
                table: "network_interfaces",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "network_skip_rules",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false),
                    Scope = table.Column<short>(type: "smallint", nullable: false),
                    LocationId = table.Column<Guid>(type: "uuid", nullable: true),
                    SensorId = table.Column<Guid>(type: "uuid", nullable: true),
                    InterfacePattern = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    InterfaceTypes = table.Column<List<string>>(type: "text[]", nullable: true),
                    Vlans = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    Evidence = table.Column<short>(type: "smallint", nullable: false),
                    MacPrefix = table.Column<string>(type: "character varying(17)", maxLength: 17, nullable: true),
                    Hits = table.Column<int>(type: "integer", nullable: false),
                    HitRouters = table.Column<int>(type: "integer", nullable: false),
                    HitsAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedByName = table.Column<string>(type: "text", nullable: true),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    UpdatedBy = table.Column<Guid>(type: "uuid", nullable: true),
                    UpdatedByName = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_network_skip_rules", x => x.Id);
                    table.ForeignKey(
                        name: "FK_network_skip_rules_locations_LocationId",
                        column: x => x.LocationId,
                        principalTable: "locations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_network_skip_rules_sensors_SensorId",
                        column: x => x.SensorId,
                        principalTable: "sensors",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_network_interfaces_SkippedRuleId",
                table: "network_interfaces",
                column: "SkippedRuleId");

            migrationBuilder.CreateIndex(
                name: "IX_network_skip_rules_LocationId",
                table: "network_skip_rules",
                column: "LocationId");

            migrationBuilder.CreateIndex(
                name: "IX_network_skip_rules_SensorId",
                table: "network_skip_rules",
                column: "SensorId");

            migrationBuilder.AddForeignKey(
                name: "FK_network_interfaces_network_skip_rules_SkippedRuleId",
                table: "network_interfaces",
                column: "SkippedRuleId",
                principalTable: "network_skip_rules",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_network_interfaces_network_skip_rules_SkippedRuleId",
                table: "network_interfaces");

            migrationBuilder.DropTable(
                name: "network_skip_rules");

            migrationBuilder.DropIndex(
                name: "IX_network_interfaces_SkippedRuleId",
                table: "network_interfaces");

            migrationBuilder.DropColumn(
                name: "SkippedRuleId",
                table: "network_interfaces");
        }
    }
}
