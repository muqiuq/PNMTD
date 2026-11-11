using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PNMTD.Data.Migrations
{
    /// <inheritdoc />
    public partial class InitialPostgres : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "hosts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    Location = table.Column<string>(type: "text", nullable: true),
                    Notes = table.Column<string>(type: "text", nullable: true),
                    Created = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_hosts", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "keyvalues",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false),
                    Note = table.Column<string>(type: "text", nullable: false),
                    Key = table.Column<string>(type: "text", nullable: false),
                    Value = table.Column<string>(type: "text", nullable: false),
                    IsReadOnly = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_keyvalues", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "notificationrules",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    Recipient = table.Column<string>(type: "text", nullable: false),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false),
                    Type = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_notificationrules", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "dnszones",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false),
                    RequiresProcessing = table.Column<bool>(type: "boolean", nullable: false),
                    ForceUpdate = table.Column<bool>(type: "boolean", nullable: false),
                    Interval = table.Column<int>(type: "integer", nullable: false),
                    LastChecked = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    NextCheck = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    RecordsMatch = table.Column<bool>(type: "boolean", nullable: false),
                    ZoneName = table.Column<string>(type: "text", nullable: false),
                    ZoneFileContent = table.Column<string>(type: "text", nullable: false),
                    HostId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_dnszones", x => x.Id);
                    table.ForeignKey(
                        name: "FK_dnszones_hosts_HostId",
                        column: x => x.HostId,
                        principalTable: "hosts",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "sensors",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OlderSiblingId = table.Column<Guid>(type: "uuid", nullable: true),
                    ParentId = table.Column<Guid>(type: "uuid", nullable: false),
                    Type = table.Column<int>(type: "integer", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    Status = table.Column<string>(type: "text", nullable: true),
                    SecretWriteToken = table.Column<string>(type: "text", nullable: false),
                    SecretReadToken = table.Column<string>(type: "text", nullable: true),
                    TextId = table.Column<string>(type: "text", nullable: true),
                    Created = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false),
                    Ignore = table.Column<bool>(type: "boolean", nullable: false),
                    Interval = table.Column<int>(type: "integer", nullable: false),
                    GracePeriod = table.Column<int>(type: "integer", nullable: false),
                    Parameters = table.Column<string>(type: "text", nullable: true),
                    Source = table.Column<string>(type: "text", nullable: true)
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
                name: "dnszoneentries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Ignore = table.Column<bool>(type: "boolean", nullable: false),
                    NumOfFailuresInARow = table.Column<int>(type: "integer", nullable: false),
                    DnsZoneId = table.Column<Guid>(type: "uuid", nullable: false),
                    TTL = table.Column<int>(type: "integer", nullable: false),
                    RecordType = table.Column<int>(type: "integer", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    ReferenceValue = table.Column<string>(type: "text", nullable: false),
                    ActualValue = table.Column<string>(type: "text", nullable: true),
                    Updated = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsMatch = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_dnszoneentries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_dnszoneentries_dnszones_DnsZoneId",
                        column: x => x.DnsZoneId,
                        principalTable: "dnszones",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "dnszonelogentries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Created = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    DnsZoneId = table.Column<Guid>(type: "uuid", nullable: true),
                    EntryType = table.Column<int>(type: "integer", nullable: false),
                    Message = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_dnszonelogentries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_dnszonelogentries_dnszones_DnsZoneId",
                        column: x => x.DnsZoneId,
                        principalTable: "dnszones",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "events",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SensorId = table.Column<Guid>(type: "uuid", nullable: false),
                    Created = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Message = table.Column<string>(type: "text", nullable: true),
                    Source = table.Column<string>(type: "text", nullable: false),
                    Code = table.Column<int>(type: "integer", nullable: false)
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
                name: "mailinputrules",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    BodyTest = table.Column<string>(type: "text", nullable: true),
                    FromTest = table.Column<string>(type: "text", nullable: true),
                    ExtractMessageRegex = table.Column<string>(type: "text", nullable: true),
                    SubjectTest = table.Column<string>(type: "text", nullable: true),
                    OkCode = table.Column<int>(type: "integer", nullable: true),
                    OkTest = table.Column<string>(type: "text", nullable: true),
                    FailCode = table.Column<int>(type: "integer", nullable: true),
                    FailTest = table.Column<string>(type: "text", nullable: true),
                    DefaultCode = table.Column<int>(type: "integer", nullable: false),
                    SensorOutputId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_mailinputrules", x => x.Id);
                    table.ForeignKey(
                        name: "FK_mailinputrules_sensors_SensorOutputId",
                        column: x => x.SensorOutputId,
                        principalTable: "sensors",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "notificationrulesensor",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    NotificationRuleId = table.Column<Guid>(type: "uuid", nullable: false),
                    SensorId = table.Column<Guid>(type: "uuid", nullable: false),
                    Parameter = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_notificationrulesensor", x => x.Id);
                    table.ForeignKey(
                        name: "FK_notificationrulesensor_notificationrules_NotificationRuleId",
                        column: x => x.NotificationRuleId,
                        principalTable: "notificationrules",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_notificationrulesensor_sensors_SensorId",
                        column: x => x.SensorId,
                        principalTable: "sensors",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "notificationruleevent",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    NoAction = table.Column<bool>(type: "boolean", nullable: false),
                    NotificationRuleId = table.Column<Guid>(type: "uuid", nullable: false),
                    EventId = table.Column<Guid>(type: "uuid", nullable: false),
                    Created = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Updated = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LastCleanupEventCreated = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
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

            migrationBuilder.CreateTable(
                name: "maillogs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    RemoteId = table.Column<string>(type: "text", nullable: false),
                    Processed = table.Column<bool>(type: "boolean", nullable: false),
                    ProcessedById = table.Column<Guid>(type: "uuid", nullable: true),
                    ProcessLog = table.Column<string>(type: "text", nullable: false),
                    Created = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    MessageDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    From = table.Column<string>(type: "text", nullable: false),
                    To = table.Column<string>(type: "text", nullable: false),
                    Subject = table.Column<string>(type: "text", nullable: false),
                    Content = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_maillogs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_maillogs_mailinputrules_ProcessedById",
                        column: x => x.ProcessedById,
                        principalTable: "mailinputrules",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateIndex(
                name: "IX_dnszoneentries_DnsZoneId",
                table: "dnszoneentries",
                column: "DnsZoneId");

            migrationBuilder.CreateIndex(
                name: "IX_dnszonelogentries_DnsZoneId",
                table: "dnszonelogentries",
                column: "DnsZoneId");

            migrationBuilder.CreateIndex(
                name: "IX_dnszones_HostId",
                table: "dnszones",
                column: "HostId");

            migrationBuilder.CreateIndex(
                name: "IX_events_Created",
                table: "events",
                column: "Created");

            migrationBuilder.CreateIndex(
                name: "IX_events_SensorId_Created",
                table: "events",
                columns: new[] { "SensorId", "Created" });

            migrationBuilder.CreateIndex(
                name: "IX_keyvalues_Key",
                table: "keyvalues",
                column: "Key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_mailinputrules_SensorOutputId",
                table: "mailinputrules",
                column: "SensorOutputId");

            migrationBuilder.CreateIndex(
                name: "IX_maillogs_ProcessedById",
                table: "maillogs",
                column: "ProcessedById");

            migrationBuilder.CreateIndex(
                name: "IX_notificationruleevent_EventId",
                table: "notificationruleevent",
                column: "EventId");

            migrationBuilder.CreateIndex(
                name: "IX_notificationruleevent_NotificationRuleId",
                table: "notificationruleevent",
                column: "NotificationRuleId");

            migrationBuilder.CreateIndex(
                name: "IX_notificationrulesensor_NotificationRuleId",
                table: "notificationrulesensor",
                column: "NotificationRuleId");

            migrationBuilder.CreateIndex(
                name: "IX_notificationrulesensor_SensorId",
                table: "notificationrulesensor",
                column: "SensorId");

            migrationBuilder.CreateIndex(
                name: "IX_sensors_OlderSiblingId",
                table: "sensors",
                column: "OlderSiblingId");

            migrationBuilder.CreateIndex(
                name: "IX_sensors_ParentId",
                table: "sensors",
                column: "ParentId");

            migrationBuilder.CreateIndex(
                name: "IX_sensors_SecretReadToken",
                table: "sensors",
                column: "SecretReadToken");

            migrationBuilder.CreateIndex(
                name: "IX_sensors_SecretWriteToken",
                table: "sensors",
                column: "SecretWriteToken");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "dnszoneentries");

            migrationBuilder.DropTable(
                name: "dnszonelogentries");

            migrationBuilder.DropTable(
                name: "keyvalues");

            migrationBuilder.DropTable(
                name: "maillogs");

            migrationBuilder.DropTable(
                name: "notificationruleevent");

            migrationBuilder.DropTable(
                name: "notificationrulesensor");

            migrationBuilder.DropTable(
                name: "dnszones");

            migrationBuilder.DropTable(
                name: "mailinputrules");

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
