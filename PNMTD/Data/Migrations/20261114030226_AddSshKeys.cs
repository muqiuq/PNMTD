using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PNMTD.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddSshKeys : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ssh_key_authorizations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SensorId = table.Column<Guid>(type: "uuid", nullable: false),
                    Account = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    Path = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false),
                    Line = table.Column<int>(type: "integer", nullable: false),
                    Fingerprint = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Type = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Bits = table.Column<int>(type: "integer", nullable: true),
                    Comment = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    PublicKey = table.Column<string>(type: "text", nullable: false),
                    Options = table.Column<string>(type: "jsonb", nullable: true),
                    Notes = table.Column<List<string>>(type: "text[]", nullable: false),
                    ClusterWide = table.Column<bool>(type: "boolean", nullable: false),
                    Removable = table.Column<bool>(type: "boolean", nullable: false),
                    NotRemovable = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    Protected = table.Column<bool>(type: "boolean", nullable: false),
                    ProtectedBecause = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    FirstSeenAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LastUsedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastUsedFrom = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ssh_key_authorizations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ssh_key_authorizations_sensors_SensorId",
                        column: x => x.SensorId,
                        principalTable: "sensors",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ssh_keys",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Fingerprint = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Type = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Bits = table.Column<int>(type: "integer", nullable: true),
                    PublicKey = table.Column<string>(type: "text", nullable: false),
                    Comment = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: true),
                    Owner = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    Kind = table.Column<int>(type: "integer", nullable: false),
                    Note = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    State = table.Column<int>(type: "integer", nullable: false),
                    RetiredAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    RetiredByName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedByName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedByName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ssh_keys", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ssh_keys_users_UserId",
                        column: x => x.UserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ssh_key_authorizations_Fingerprint",
                table: "ssh_key_authorizations",
                column: "Fingerprint");

            migrationBuilder.CreateIndex(
                name: "IX_ssh_key_authorizations_SensorId_Account_Path_Fingerprint",
                table: "ssh_key_authorizations",
                columns: new[] { "SensorId", "Account", "Path", "Fingerprint" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ssh_keys_Fingerprint",
                table: "ssh_keys",
                column: "Fingerprint",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ssh_keys_UserId",
                table: "ssh_keys",
                column: "UserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ssh_key_authorizations");

            migrationBuilder.DropTable(
                name: "ssh_keys");
        }
    }
}
