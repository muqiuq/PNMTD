using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PNMTD.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "hosts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    Location = table.Column<string>(type: "TEXT", nullable: true),
                    Notes = table.Column<string>(type: "TEXT", nullable: true),
                    Created = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_hosts", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "notificationrules",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    Recipient = table.Column<string>(type: "TEXT", nullable: false),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    Type = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_notificationrules", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "sensors",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    OlderSiblingId = table.Column<Guid>(type: "TEXT", nullable: true),
                    ParentId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Type = table.Column<int>(type: "INTEGER", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    TextId = table.Column<string>(type: "TEXT", nullable: true),
                    Created = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    Interval = table.Column<int>(type: "INTEGER", nullable: false),
                    GracePeriod = table.Column<int>(type: "INTEGER", nullable: false),
                    Parameters = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_sensors", x => x.Id);
                    table.ForeignKey(
                        name: "FK_sensors_hosts_ParentId",
                        column: x => x.ParentId,
                        principalTable: "hosts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_sensors_sensors_OlderSiblingId",
                        column: x => x.OlderSiblingId,
                        principalTable: "sensors",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "events",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    SensorId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Created = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Message = table.Column<string>(type: "TEXT", nullable: true),
                    Source = table.Column<string>(type: "TEXT", nullable: false),
                    Code = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_events", x => x.Id);
                    table.ForeignKey(
                        name: "FK_events_sensors_SensorId",
                        column: x => x.SensorId,
                        principalTable: "sensors",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "NotificationRuleSensor",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    NotificationRuleId = table.Column<Guid>(type: "TEXT", nullable: false),
                    SensorId = table.Column<Guid>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NotificationRuleSensor", x => x.Id);
                    table.ForeignKey(
                        name: "FK_NotificationRuleSensor_notificationrules_NotificationRuleId",
                        column: x => x.NotificationRuleId,
                        principalTable: "notificationrules",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_NotificationRuleSensor_sensors_SensorId",
                        column: x => x.SensorId,
                        principalTable: "sensors",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "notificationruleevent",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    NoAction = table.Column<bool>(type: "INTEGER", nullable: false),
                    NotificationRuleId = table.Column<Guid>(type: "TEXT", nullable: false),
                    EventId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Created = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_notificationruleevent", x => x.Id);
                    table.ForeignKey(
                        name: "FK_notificationruleevent_events_EventId",
                        column: x => x.EventId,
                        principalTable: "events",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_notificationruleevent_notificationrules_NotificationRuleId",
                        column: x => x.NotificationRuleId,
                        principalTable: "notificationrules",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_events_SensorId",
                table: "events",
                column: "SensorId");

            migrationBuilder.CreateIndex(
                name: "IX_notificationruleevent_EventId",
                table: "notificationruleevent",
                column: "EventId");

            migrationBuilder.CreateIndex(
                name: "IX_notificationruleevent_NotificationRuleId",
                table: "notificationruleevent",
                column: "NotificationRuleId");

            migrationBuilder.CreateIndex(
                name: "IX_NotificationRuleSensor_NotificationRuleId",
                table: "NotificationRuleSensor",
                column: "NotificationRuleId");

            migrationBuilder.CreateIndex(
                name: "IX_NotificationRuleSensor_SensorId",
                table: "NotificationRuleSensor",
                column: "SensorId");

            migrationBuilder.CreateIndex(
                name: "IX_sensors_OlderSiblingId",
                table: "sensors",
                column: "OlderSiblingId");

            migrationBuilder.CreateIndex(
                name: "IX_sensors_ParentId",
                table: "sensors",
                column: "ParentId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "notificationruleevent");

            migrationBuilder.DropTable(
                name: "NotificationRuleSensor");

            migrationBuilder.DropTable(
                name: "events");

            migrationBuilder.DropTable(
                name: "notificationrules");

            migrationBuilder.DropTable(
                name: "sensors");

            migrationBuilder.DropTable(
                name: "hosts");
        }
    }
}
