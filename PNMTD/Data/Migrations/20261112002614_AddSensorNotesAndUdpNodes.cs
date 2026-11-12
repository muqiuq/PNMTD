using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace PNMTD.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddSensorNotesAndUdpNodes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Notes",
                table: "sensors",
                type: "text",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "udp_commands",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SensorId = table.Column<Guid>(type: "uuid", nullable: true),
                    DeviceId = table.Column<long>(type: "bigint", nullable: false),
                    NodeName = table.Column<string>(type: "text", nullable: false),
                    CmdId = table.Column<short>(type: "smallint", nullable: false),
                    Payload = table.Column<byte[]>(type: "bytea", nullable: false),
                    State = table.Column<short>(type: "smallint", nullable: false),
                    Delivery = table.Column<short>(type: "smallint", nullable: false),
                    Suite = table.Column<byte>(type: "smallint", nullable: true),
                    MaxAttempts = table.Column<short>(type: "smallint", nullable: false),
                    Attempts = table.Column<short>(type: "smallint", nullable: false),
                    Created = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedByName = table.Column<string>(type: "text", nullable: true),
                    ExpiresAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastSentAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CompletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ResultStatus = table.Column<short>(type: "smallint", nullable: true),
                    Result = table.Column<byte[]>(type: "bytea", nullable: true),
                    Note = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_udp_commands", x => x.Id);
                    table.ForeignKey(
                        name: "FK_udp_commands_sensors_SensorId",
                        column: x => x.SensorId,
                        principalTable: "sensors",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "udp_firmware",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    Version = table.Column<string>(type: "text", nullable: false),
                    FileName = table.Column<string>(type: "text", nullable: false),
                    Size = table.Column<int>(type: "integer", nullable: false),
                    Sha256 = table.Column<byte[]>(type: "bytea", nullable: false),
                    Content = table.Column<byte[]>(type: "bytea", nullable: false),
                    Notes = table.Column<string>(type: "text", nullable: true),
                    UploadedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UploadedBy = table.Column<Guid>(type: "uuid", nullable: true),
                    UploadedByName = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_udp_firmware", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "udp_last_packets",
                columns: table => new
                {
                    SensorId = table.Column<Guid>(type: "uuid", nullable: false),
                    Type = table.Column<byte>(type: "smallint", nullable: false),
                    ReceivedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Endpoint = table.Column<string>(type: "text", nullable: false),
                    Suite = table.Column<byte>(type: "smallint", nullable: false),
                    Fcnt = table.Column<long>(type: "bigint", nullable: false),
                    Datagram = table.Column<byte[]>(type: "bytea", nullable: false),
                    Payload = table.Column<byte[]>(type: "bytea", nullable: true),
                    Response = table.Column<byte[]>(type: "bytea", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_udp_last_packets", x => new { x.SensorId, x.Type });
                    table.ForeignKey(
                        name: "FK_udp_last_packets_sensors_SensorId",
                        column: x => x.SensorId,
                        principalTable: "sensors",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "udp_nodes",
                columns: table => new
                {
                    SensorId = table.Column<Guid>(type: "uuid", nullable: false),
                    DeviceId = table.Column<long>(type: "bigint", nullable: false),
                    KeyFamily = table.Column<short>(type: "smallint", nullable: false),
                    AllowedSuites = table.Column<int>(type: "integer", nullable: false),
                    Key = table.Column<byte[]>(type: "bytea", nullable: false),
                    Token = table.Column<byte[]>(type: "bytea", nullable: false),
                    KeySource = table.Column<short>(type: "smallint", nullable: false),
                    TokenSource = table.Column<short>(type: "smallint", nullable: false),
                    KeyChangedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    KeyChangedBy = table.Column<Guid>(type: "uuid", nullable: true),
                    TokenChangedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    TokenChangedBy = table.Column<Guid>(type: "uuid", nullable: true),
                    SessionMode = table.Column<short>(type: "smallint", nullable: false),
                    SessionKey = table.Column<byte[]>(type: "bytea", nullable: true),
                    SessionStartedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    PendingSessionKey = table.Column<byte[]>(type: "bytea", nullable: true),
                    PendingSessionAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    AckMode = table.Column<short>(type: "smallint", nullable: false),
                    ReplayProtection = table.Column<bool>(type: "boolean", nullable: false),
                    ResponseSuite = table.Column<byte>(type: "smallint", nullable: true),
                    FcntHigh = table.Column<long>(type: "bigint", nullable: true),
                    FcntWindow = table.Column<long>(type: "bigint", nullable: false),
                    LastUplinkFcnt = table.Column<long>(type: "bigint", nullable: true),
                    LastUplinkDigest = table.Column<byte[]>(type: "bytea", nullable: true),
                    LastUplinkAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastResponse = table.Column<byte[]>(type: "bytea", nullable: true),
                    LastEndpoint = table.Column<string>(type: "text", nullable: true),
                    LastSuite = table.Column<byte>(type: "smallint", nullable: true),
                    LastSeenAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastBootAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastBootReason = table.Column<byte>(type: "smallint", nullable: true),
                    NextCmdId = table.Column<short>(type: "smallint", nullable: false),
                    NextUpdateId = table.Column<short>(type: "smallint", nullable: false),
                    FirmwareVersion = table.Column<string>(type: "text", nullable: true),
                    Created = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_udp_nodes", x => x.SensorId);
                    table.ForeignKey(
                        name: "FK_udp_nodes_sensors_SensorId",
                        column: x => x.SensorId,
                        principalTable: "sensors",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "udp_ota_jobs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SensorId = table.Column<Guid>(type: "uuid", nullable: true),
                    DeviceId = table.Column<long>(type: "bigint", nullable: false),
                    NodeName = table.Column<string>(type: "text", nullable: false),
                    FirmwareId = table.Column<Guid>(type: "uuid", nullable: true),
                    FirmwareName = table.Column<string>(type: "text", nullable: false),
                    FirmwareVersion = table.Column<string>(type: "text", nullable: false),
                    FirmwareSize = table.Column<int>(type: "integer", nullable: false),
                    FirmwareSha256 = table.Column<byte[]>(type: "bytea", nullable: false),
                    UpdateId = table.Column<short>(type: "smallint", nullable: false),
                    ChunkSize = table.Column<short>(type: "smallint", nullable: false),
                    State = table.Column<short>(type: "smallint", nullable: false),
                    Reason = table.Column<short>(type: "smallint", nullable: true),
                    OfferAttempts = table.Column<short>(type: "smallint", nullable: false),
                    MaxOfferAttempts = table.Column<short>(type: "smallint", nullable: false),
                    BytesServed = table.Column<long>(type: "bigint", nullable: false),
                    ChunksServed = table.Column<int>(type: "integer", nullable: false),
                    ChunkRetransmissions = table.Column<int>(type: "integer", nullable: false),
                    HighestOffset = table.Column<long>(type: "bigint", nullable: false),
                    Created = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedByName = table.Column<string>(type: "text", nullable: true),
                    OfferedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    StartedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastRequestAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    FinishedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ExpiresAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    AppliedVersion = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_udp_ota_jobs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_udp_ota_jobs_sensors_SensorId",
                        column: x => x.SensorId,
                        principalTable: "sensors",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_udp_ota_jobs_udp_firmware_FirmwareId",
                        column: x => x.FirmwareId,
                        principalTable: "udp_firmware",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "udp_command_log",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Created = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CommandId = table.Column<Guid>(type: "uuid", nullable: true),
                    OtaJobId = table.Column<Guid>(type: "uuid", nullable: true),
                    SensorId = table.Column<Guid>(type: "uuid", nullable: true),
                    DeviceId = table.Column<long>(type: "bigint", nullable: false),
                    NodeName = table.Column<string>(type: "text", nullable: false),
                    Kind = table.Column<short>(type: "smallint", nullable: false),
                    Direction = table.Column<short>(type: "smallint", nullable: true),
                    CmdId = table.Column<short>(type: "smallint", nullable: true),
                    Attempt = table.Column<short>(type: "smallint", nullable: true),
                    Endpoint = table.Column<string>(type: "text", nullable: true),
                    Suite = table.Column<byte>(type: "smallint", nullable: true),
                    Fcnt = table.Column<long>(type: "bigint", nullable: true),
                    Datagram = table.Column<byte[]>(type: "bytea", nullable: true),
                    Payload = table.Column<byte[]>(type: "bytea", nullable: true),
                    ResultStatus = table.Column<short>(type: "smallint", nullable: true),
                    UserId = table.Column<Guid>(type: "uuid", nullable: true),
                    UserName = table.Column<string>(type: "text", nullable: true),
                    Note = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_udp_command_log", x => x.Id);
                    table.ForeignKey(
                        name: "FK_udp_command_log_sensors_SensorId",
                        column: x => x.SensorId,
                        principalTable: "sensors",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_udp_command_log_udp_commands_CommandId",
                        column: x => x.CommandId,
                        principalTable: "udp_commands",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_udp_command_log_udp_ota_jobs_OtaJobId",
                        column: x => x.OtaJobId,
                        principalTable: "udp_ota_jobs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_udp_command_log_CommandId_Created",
                table: "udp_command_log",
                columns: new[] { "CommandId", "Created" });

            migrationBuilder.CreateIndex(
                name: "IX_udp_command_log_Created",
                table: "udp_command_log",
                column: "Created");

            migrationBuilder.CreateIndex(
                name: "IX_udp_command_log_OtaJobId_Created",
                table: "udp_command_log",
                columns: new[] { "OtaJobId", "Created" });

            migrationBuilder.CreateIndex(
                name: "IX_udp_command_log_SensorId_Created",
                table: "udp_command_log",
                columns: new[] { "SensorId", "Created" });

            migrationBuilder.CreateIndex(
                name: "IX_udp_commands_SensorId_State_Created",
                table: "udp_commands",
                columns: new[] { "SensorId", "State", "Created" });

            migrationBuilder.CreateIndex(
                name: "IX_udp_nodes_DeviceId",
                table: "udp_nodes",
                column: "DeviceId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_udp_ota_jobs_Created",
                table: "udp_ota_jobs",
                column: "Created");

            migrationBuilder.CreateIndex(
                name: "IX_udp_ota_jobs_FirmwareId",
                table: "udp_ota_jobs",
                column: "FirmwareId");

            migrationBuilder.CreateIndex(
                name: "IX_udp_ota_jobs_SensorId_State_Created",
                table: "udp_ota_jobs",
                columns: new[] { "SensorId", "State", "Created" });

            // Firmware images uncompressed and out of line: reading a range reads only its pages (§4.11).
            migrationBuilder.Sql("ALTER TABLE udp_firmware ALTER COLUMN \"Content\" SET STORAGE EXTERNAL;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "udp_command_log");

            migrationBuilder.DropTable(
                name: "udp_last_packets");

            migrationBuilder.DropTable(
                name: "udp_nodes");

            migrationBuilder.DropTable(
                name: "udp_commands");

            migrationBuilder.DropTable(
                name: "udp_ota_jobs");

            migrationBuilder.DropTable(
                name: "udp_firmware");

            migrationBuilder.DropColumn(
                name: "Notes",
                table: "sensors");
        }
    }
}
