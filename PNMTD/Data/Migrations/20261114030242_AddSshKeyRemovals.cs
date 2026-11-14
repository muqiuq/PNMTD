using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PNMTD.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddSshKeyRemovals : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ssh_key_removals",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    BatchId = table.Column<Guid>(type: "uuid", nullable: false),
                    SensorId = table.Column<Guid>(type: "uuid", nullable: true),
                    ServerName = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    Account = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    Path = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false),
                    Fingerprint = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    KeyName = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    RequestedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    RequestedBy = table.Column<Guid>(type: "uuid", nullable: true),
                    RequestedByName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    Reason = table.Column<int>(type: "integer", nullable: false),
                    State = table.Column<int>(type: "integer", nullable: false),
                    SentAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LinesRemoved = table.Column<int>(type: "integer", nullable: true),
                    Backup = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    NewFileHash = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    Message = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    AppliedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ssh_key_removals", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ssh_key_removals_sensors_SensorId",
                        column: x => x.SensorId,
                        principalTable: "sensors",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ssh_key_removals_BatchId",
                table: "ssh_key_removals",
                column: "BatchId");

            migrationBuilder.CreateIndex(
                name: "IX_ssh_key_removals_Fingerprint",
                table: "ssh_key_removals",
                column: "Fingerprint");

            migrationBuilder.CreateIndex(
                name: "IX_ssh_key_removals_RequestedAt",
                table: "ssh_key_removals",
                column: "RequestedAt");

            migrationBuilder.CreateIndex(
                name: "IX_ssh_key_removals_SensorId_State",
                table: "ssh_key_removals",
                columns: new[] { "SensorId", "State" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ssh_key_removals");
        }
    }
}
