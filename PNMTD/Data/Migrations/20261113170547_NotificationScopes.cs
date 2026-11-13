using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PNMTD.Data.Migrations
{
    /// <inheritdoc />
    public partial class NotificationScopes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "notification_rule_exclusions",
                columns: table => new
                {
                    RuleId = table.Column<Guid>(type: "uuid", nullable: false),
                    SensorId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_notification_rule_exclusions", x => new { x.RuleId, x.SensorId });
                    table.ForeignKey(
                        name: "FK_notification_rule_exclusions_notificationrules_RuleId",
                        column: x => x.RuleId,
                        principalTable: "notificationrules",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_notification_rule_exclusions_sensors_SensorId",
                        column: x => x.SensorId,
                        principalTable: "sensors",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "notification_rule_scopes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    RuleId = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<short>(type: "smallint", nullable: false),
                    Importance = table.Column<short>(type: "smallint", nullable: true),
                    HostId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_notification_rule_scopes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_notification_rule_scopes_hosts_HostId",
                        column: x => x.HostId,
                        principalTable: "hosts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_notification_rule_scopes_notificationrules_RuleId",
                        column: x => x.RuleId,
                        principalTable: "notificationrules",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_notification_rule_exclusions_SensorId",
                table: "notification_rule_exclusions",
                column: "SensorId");

            migrationBuilder.CreateIndex(
                name: "IX_notification_rule_scopes_HostId",
                table: "notification_rule_scopes",
                column: "HostId");

            migrationBuilder.CreateIndex(
                name: "IX_notification_rule_scopes_RuleId",
                table: "notification_rule_scopes",
                column: "RuleId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "notification_rule_exclusions");

            migrationBuilder.DropTable(
                name: "notification_rule_scopes");
        }
    }
}
