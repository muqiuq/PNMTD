using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace PNMTD.Data.Migrations
{
    /// <inheritdoc />
    public partial class NetworkMap : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "agent_adapter_macs",
                columns: table => new
                {
                    SensorId = table.Column<Guid>(type: "uuid", nullable: false),
                    Mac = table.Column<string>(type: "character varying(17)", maxLength: 17, nullable: false),
                    Adapter = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Kind = table.Column<string>(type: "text", nullable: true),
                    SpeedBps = table.Column<long>(type: "bigint", nullable: true),
                    Virtual = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_agent_adapter_macs", x => new { x.SensorId, x.Mac, x.Adapter });
                    table.ForeignKey(
                        name: "FK_agent_adapter_macs_sensors_SensorId",
                        column: x => x.SensorId,
                        principalTable: "sensors",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "network_connections",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Key = table.Column<string>(type: "text", nullable: false),
                    AHostId = table.Column<Guid>(type: "uuid", nullable: false),
                    ASensorId = table.Column<Guid>(type: "uuid", nullable: true),
                    AInterface = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    BHostId = table.Column<Guid>(type: "uuid", nullable: false),
                    BSensorId = table.Column<Guid>(type: "uuid", nullable: true),
                    BInterface = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    Type = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Notes = table.Column<string>(type: "text", nullable: true),
                    Source = table.Column<short>(type: "smallint", nullable: false),
                    Evidence = table.Column<string>(type: "jsonb", nullable: true),
                    ConfirmedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    UnconfirmedSince = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Misses = table.Column<int>(type: "integer", nullable: false),
                    CheckedScans = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedByName = table.Column<string>(type: "text", nullable: true),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    UpdatedBy = table.Column<Guid>(type: "uuid", nullable: true),
                    UpdatedByName = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_network_connections", x => x.Id);
                    table.ForeignKey(
                        name: "FK_network_connections_hosts_AHostId",
                        column: x => x.AHostId,
                        principalTable: "hosts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_network_connections_hosts_BHostId",
                        column: x => x.BHostId,
                        principalTable: "hosts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_network_connections_sensors_ASensorId",
                        column: x => x.ASensorId,
                        principalTable: "sensors",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_network_connections_sensors_BSensorId",
                        column: x => x.BSensorId,
                        principalTable: "sensors",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "network_interfaces",
                columns: table => new
                {
                    Mac = table.Column<string>(type: "character varying(17)", maxLength: 17, nullable: false),
                    Ips = table.Column<List<string>>(type: "text[]", nullable: false),
                    HostName = table.Column<string>(type: "text", nullable: true),
                    Random = table.Column<bool>(type: "boolean", nullable: false),
                    Ignored = table.Column<bool>(type: "boolean", nullable: false),
                    Sources = table.Column<int>(type: "integer", nullable: false),
                    AttachedSensorId = table.Column<Guid>(type: "uuid", nullable: true),
                    AttachedPort = table.Column<string>(type: "text", nullable: true),
                    AttachedVlan = table.Column<int>(type: "integer", nullable: true),
                    AttachedNote = table.Column<string>(type: "text", nullable: true),
                    NeighborIdentity = table.Column<string>(type: "text", nullable: true),
                    NeighborPlatform = table.Column<string>(type: "text", nullable: true),
                    NeighborBoard = table.Column<string>(type: "text", nullable: true),
                    NeighborPort = table.Column<string>(type: "text", nullable: true),
                    MatchKind = table.Column<int>(type: "integer", nullable: false),
                    MatchHostId = table.Column<Guid>(type: "uuid", nullable: true),
                    MatchSensorId = table.Column<Guid>(type: "uuid", nullable: true),
                    MatchInterface = table.Column<string>(type: "text", nullable: true),
                    HintHostId = table.Column<Guid>(type: "uuid", nullable: true),
                    AssignedHostId = table.Column<Guid>(type: "uuid", nullable: true),
                    AssignedInterface = table.Column<string>(type: "text", nullable: true),
                    AssignedBy = table.Column<Guid>(type: "uuid", nullable: true),
                    AssignedByName = table.Column<string>(type: "text", nullable: true),
                    AssignedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    FirstSeenAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LastSeenAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_network_interfaces", x => x.Mac);
                    table.ForeignKey(
                        name: "FK_network_interfaces_hosts_AssignedHostId",
                        column: x => x.AssignedHostId,
                        principalTable: "hosts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "network_map_settings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false),
                    ScanScheduleEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    ScanIntervalDays = table.Column<int>(type: "integer", nullable: false),
                    ScanHour = table.Column<int>(type: "integer", nullable: false),
                    RetentionDays = table.Column<int>(type: "integer", nullable: false),
                    ChangedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ChangedBy = table.Column<Guid>(type: "uuid", nullable: true),
                    ChangedByName = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_network_map_settings", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "network_observations",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    SensorId = table.Column<Guid>(type: "uuid", nullable: false),
                    Source = table.Column<int>(type: "integer", nullable: false),
                    Interface = table.Column<string>(type: "text", nullable: true),
                    Bridge = table.Column<string>(type: "text", nullable: true),
                    VlanId = table.Column<int>(type: "integer", nullable: true),
                    Mac = table.Column<string>(type: "character varying(17)", maxLength: 17, nullable: true),
                    Ip = table.Column<string>(type: "text", nullable: true),
                    HostName = table.Column<string>(type: "text", nullable: true),
                    Identity = table.Column<string>(type: "text", nullable: true),
                    Platform = table.Column<string>(type: "text", nullable: true),
                    Board = table.Column<string>(type: "text", nullable: true),
                    Version = table.Column<string>(type: "text", nullable: true),
                    RemoteInterface = table.Column<string>(type: "text", nullable: true),
                    DiscoveredBy = table.Column<string>(type: "text", nullable: true),
                    RemoteKey = table.Column<string>(type: "text", nullable: true),
                    Note = table.Column<string>(type: "text", nullable: true),
                    SeenAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_network_observations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_network_observations_sensors_SensorId",
                        column: x => x.SensorId,
                        principalTable: "sensors",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "network_router_ports",
                columns: table => new
                {
                    SensorId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    DefaultName = table.Column<string>(type: "text", nullable: true),
                    Type = table.Column<string>(type: "text", nullable: true),
                    Mac = table.Column<string>(type: "character varying(17)", maxLength: 17, nullable: true),
                    Mtu = table.Column<int>(type: "integer", nullable: true),
                    Running = table.Column<bool>(type: "boolean", nullable: false),
                    Disabled = table.Column<bool>(type: "boolean", nullable: false),
                    Comment = table.Column<string>(type: "text", nullable: true),
                    Bridge = table.Column<string>(type: "text", nullable: true),
                    VlanParent = table.Column<string>(type: "text", nullable: true),
                    VlanId = table.Column<int>(type: "integer", nullable: true),
                    Bond = table.Column<string>(type: "text", nullable: true),
                    RateMbps = table.Column<int>(type: "integer", nullable: true),
                    FullDuplex = table.Column<bool>(type: "boolean", nullable: true),
                    Sfp = table.Column<string>(type: "jsonb", nullable: true),
                    DetectedType = table.Column<string>(type: "text", nullable: true),
                    Role = table.Column<int>(type: "integer", nullable: false),
                    MacCount = table.Column<int>(type: "integer", nullable: false),
                    SeenAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_network_router_ports", x => new { x.SensorId, x.Name });
                    table.ForeignKey(
                        name: "FK_network_router_ports_sensors_SensorId",
                        column: x => x.SensorId,
                        principalTable: "sensors",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "network_router_snapshots",
                columns: table => new
                {
                    SensorId = table.Column<Guid>(type: "uuid", nullable: false),
                    ScannedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ScanNumber = table.Column<int>(type: "integer", nullable: false),
                    RouterOsVersion = table.Column<string>(type: "text", nullable: true),
                    Raw = table.Column<byte[]>(type: "bytea", nullable: false),
                    DiscoverySettings = table.Column<string>(type: "jsonb", nullable: true),
                    TableErrors = table.Column<string>(type: "jsonb", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_network_router_snapshots", x => x.SensorId);
                    table.ForeignKey(
                        name: "FK_network_router_snapshots_sensors_SensorId",
                        column: x => x.SensorId,
                        principalTable: "sensors",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "network_scans",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Scope = table.Column<short>(type: "smallint", nullable: false),
                    LocationId = table.Column<Guid>(type: "uuid", nullable: true),
                    SensorId = table.Column<Guid>(type: "uuid", nullable: true),
                    ScopeName = table.Column<string>(type: "text", nullable: false),
                    Trigger = table.Column<short>(type: "smallint", nullable: false),
                    RequestedBy = table.Column<Guid>(type: "uuid", nullable: true),
                    RequestedByName = table.Column<string>(type: "text", nullable: true),
                    State = table.Column<short>(type: "smallint", nullable: false),
                    Total = table.Column<int>(type: "integer", nullable: false),
                    Succeeded = table.Column<int>(type: "integer", nullable: false),
                    Failed = table.Column<int>(type: "integer", nullable: false),
                    Skipped = table.Column<int>(type: "integer", nullable: false),
                    CurrentSensorId = table.Column<Guid>(type: "uuid", nullable: true),
                    CurrentRouterName = table.Column<string>(type: "text", nullable: true),
                    CurrentStep = table.Column<string>(type: "text", nullable: true),
                    CurrentStepNumber = table.Column<int>(type: "integer", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    StartedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    FinishedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_network_scans", x => x.Id);
                    table.ForeignKey(
                        name: "FK_network_scans_locations_LocationId",
                        column: x => x.LocationId,
                        principalTable: "locations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_network_scans_sensors_SensorId",
                        column: x => x.SensorId,
                        principalTable: "sensors",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "network_suggestions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Key = table.Column<string>(type: "text", nullable: false),
                    Kind = table.Column<int>(type: "integer", nullable: false),
                    AHostId = table.Column<Guid>(type: "uuid", nullable: false),
                    ASensorId = table.Column<Guid>(type: "uuid", nullable: true),
                    AInterface = table.Column<string>(type: "text", nullable: true),
                    BHostId = table.Column<Guid>(type: "uuid", nullable: false),
                    BSensorId = table.Column<Guid>(type: "uuid", nullable: true),
                    BInterface = table.Column<string>(type: "text", nullable: true),
                    Type = table.Column<string>(type: "text", nullable: true),
                    Confidence = table.Column<int>(type: "integer", nullable: false),
                    Evidence = table.Column<string>(type: "jsonb", nullable: false),
                    DiffersFrom = table.Column<Guid>(type: "uuid", nullable: true),
                    State = table.Column<short>(type: "smallint", nullable: false),
                    ConnectionId = table.Column<Guid>(type: "uuid", nullable: true),
                    DecidedBy = table.Column<Guid>(type: "uuid", nullable: true),
                    DecidedByName = table.Column<string>(type: "text", nullable: true),
                    DecidedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    FirstSeenAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LastSeenAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_network_suggestions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_network_suggestions_hosts_AHostId",
                        column: x => x.AHostId,
                        principalTable: "hosts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_network_suggestions_hosts_BHostId",
                        column: x => x.BHostId,
                        principalTable: "hosts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_network_suggestions_network_connections_ConnectionId",
                        column: x => x.ConnectionId,
                        principalTable: "network_connections",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_network_suggestions_sensors_ASensorId",
                        column: x => x.ASensorId,
                        principalTable: "sensors",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_network_suggestions_sensors_BSensorId",
                        column: x => x.BSensorId,
                        principalTable: "sensors",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "network_scan_items",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ScanId = table.Column<Guid>(type: "uuid", nullable: false),
                    SensorId = table.Column<Guid>(type: "uuid", nullable: true),
                    RouterName = table.Column<string>(type: "text", nullable: false),
                    Position = table.Column<int>(type: "integer", nullable: false),
                    State = table.Column<short>(type: "smallint", nullable: false),
                    Step = table.Column<string>(type: "text", nullable: true),
                    StartedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    FinishedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Error = table.Column<string>(type: "text", nullable: true),
                    TableErrors = table.Column<string>(type: "jsonb", nullable: true),
                    Counts = table.Column<string>(type: "jsonb", nullable: true),
                    DurationMs = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_network_scan_items", x => x.Id);
                    table.ForeignKey(
                        name: "FK_network_scan_items_network_scans_ScanId",
                        column: x => x.ScanId,
                        principalTable: "network_scans",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_network_scan_items_sensors_SensorId",
                        column: x => x.SensorId,
                        principalTable: "sensors",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "IX_agent_adapter_macs_Mac",
                table: "agent_adapter_macs",
                column: "Mac");

            migrationBuilder.CreateIndex(
                name: "IX_network_connections_AHostId",
                table: "network_connections",
                column: "AHostId");

            migrationBuilder.CreateIndex(
                name: "IX_network_connections_ASensorId",
                table: "network_connections",
                column: "ASensorId");

            migrationBuilder.CreateIndex(
                name: "IX_network_connections_BHostId",
                table: "network_connections",
                column: "BHostId");

            migrationBuilder.CreateIndex(
                name: "IX_network_connections_BSensorId",
                table: "network_connections",
                column: "BSensorId");

            migrationBuilder.CreateIndex(
                name: "IX_network_connections_Key",
                table: "network_connections",
                column: "Key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_network_interfaces_AssignedHostId",
                table: "network_interfaces",
                column: "AssignedHostId");

            migrationBuilder.CreateIndex(
                name: "IX_network_interfaces_AttachedSensorId",
                table: "network_interfaces",
                column: "AttachedSensorId");

            migrationBuilder.CreateIndex(
                name: "IX_network_interfaces_LastSeenAt",
                table: "network_interfaces",
                column: "LastSeenAt");

            migrationBuilder.CreateIndex(
                name: "IX_network_interfaces_MatchHostId",
                table: "network_interfaces",
                column: "MatchHostId");

            migrationBuilder.CreateIndex(
                name: "IX_network_observations_Mac",
                table: "network_observations",
                column: "Mac");

            migrationBuilder.CreateIndex(
                name: "IX_network_observations_SensorId_Source",
                table: "network_observations",
                columns: new[] { "SensorId", "Source" });

            migrationBuilder.CreateIndex(
                name: "IX_network_router_ports_Mac",
                table: "network_router_ports",
                column: "Mac");

            migrationBuilder.CreateIndex(
                name: "IX_network_scan_items_ScanId_Position",
                table: "network_scan_items",
                columns: new[] { "ScanId", "Position" });

            migrationBuilder.CreateIndex(
                name: "IX_network_scan_items_SensorId_FinishedAt",
                table: "network_scan_items",
                columns: new[] { "SensorId", "FinishedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_network_scan_items_State_SensorId",
                table: "network_scan_items",
                columns: new[] { "State", "SensorId" });

            migrationBuilder.CreateIndex(
                name: "IX_network_scans_CreatedAt",
                table: "network_scans",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_network_scans_LocationId",
                table: "network_scans",
                column: "LocationId");

            migrationBuilder.CreateIndex(
                name: "IX_network_scans_SensorId",
                table: "network_scans",
                column: "SensorId");

            migrationBuilder.CreateIndex(
                name: "IX_network_scans_State_CreatedAt",
                table: "network_scans",
                columns: new[] { "State", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_network_suggestions_AHostId",
                table: "network_suggestions",
                column: "AHostId");

            migrationBuilder.CreateIndex(
                name: "IX_network_suggestions_ASensorId",
                table: "network_suggestions",
                column: "ASensorId");

            migrationBuilder.CreateIndex(
                name: "IX_network_suggestions_BHostId",
                table: "network_suggestions",
                column: "BHostId");

            migrationBuilder.CreateIndex(
                name: "IX_network_suggestions_BSensorId",
                table: "network_suggestions",
                column: "BSensorId");

            migrationBuilder.CreateIndex(
                name: "IX_network_suggestions_ConnectionId",
                table: "network_suggestions",
                column: "ConnectionId");

            migrationBuilder.CreateIndex(
                name: "IX_network_suggestions_Key",
                table: "network_suggestions",
                column: "Key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_network_suggestions_State_Confidence",
                table: "network_suggestions",
                columns: new[] { "State", "Confidence" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "agent_adapter_macs");

            migrationBuilder.DropTable(
                name: "network_interfaces");

            migrationBuilder.DropTable(
                name: "network_map_settings");

            migrationBuilder.DropTable(
                name: "network_observations");

            migrationBuilder.DropTable(
                name: "network_router_ports");

            migrationBuilder.DropTable(
                name: "network_router_snapshots");

            migrationBuilder.DropTable(
                name: "network_scan_items");

            migrationBuilder.DropTable(
                name: "network_suggestions");

            migrationBuilder.DropTable(
                name: "network_scans");

            migrationBuilder.DropTable(
                name: "network_connections");
        }
    }
}
