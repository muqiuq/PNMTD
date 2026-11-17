using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PNMTD.Data.Migrations
{
    /// <summary>
    /// SSL certificate sensors (docs/ssl-certificate-plan.md §4): each sensor's endpoint, trust, rules, state and baseline; the
    /// certificates it has seen; the private CAs and which sensors trust them. A CA in use can't be deleted.
    /// </summary>
    public partial class CertificateSensors : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "certificate_authorities",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Pem = table.Column<string>(type: "text", nullable: false),
                    RootSha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    RootSubject = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    RootNotBefore = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    RootNotAfter = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IntermediateCount = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedBy = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_certificate_authorities", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "certificate_versions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SensorId = table.Column<Guid>(type: "uuid", nullable: false),
                    Sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    SpkiSha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Subject = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    SubjectCommonName = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    Issuer = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    IssuerOrganization = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    IssuerCommonName = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    Serial = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    NotBefore = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    NotAfter = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Names = table.Column<List<string>>(type: "text[]", nullable: false),
                    SelfSigned = table.Column<bool>(type: "boolean", nullable: false),
                    Facts = table.Column<string>(type: "jsonb", nullable: false),
                    Chain = table.Column<string>(type: "jsonb", nullable: false),
                    ChainPem = table.Column<string>(type: "text", nullable: false),
                    FirstSeenAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LastSeenAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_certificate_versions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_certificate_versions_sensors_SensorId",
                        column: x => x.SensorId,
                        principalTable: "sensors",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "certificate_sensors",
                columns: table => new
                {
                    SensorId = table.Column<Guid>(type: "uuid", nullable: false),
                    ServerName = table.Column<string>(type: "character varying(253)", maxLength: 253, nullable: true),
                    ConnectTo = table.Column<string>(type: "character varying(253)", maxLength: 253, nullable: true),
                    Trust = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    CheckName = table.Column<bool>(type: "boolean", nullable: false),
                    CheckExpiry = table.Column<bool>(type: "boolean", nullable: false),
                    IncompleteChainFails = table.Column<bool>(type: "boolean", nullable: false),
                    WarnDays = table.Column<int>(type: "integer", nullable: false),
                    AlertOnCertificateChange = table.Column<bool>(type: "boolean", nullable: false),
                    AlertOnKeyChange = table.Column<bool>(type: "boolean", nullable: false),
                    AlertOnIssuerChange = table.Column<bool>(type: "boolean", nullable: false),
                    IssuerMatch = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    NextCheckAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastCheckAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastSuccessAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ConsecutiveFailures = table.Column<int>(type: "integer", nullable: false),
                    FirstFailureAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastError = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    CurrentVersionId = table.Column<Guid>(type: "uuid", nullable: true),
                    Connection = table.Column<string>(type: "jsonb", nullable: true),
                    Validation = table.Column<string>(type: "jsonb", nullable: true),
                    AnnouncedVersionId = table.Column<Guid>(type: "uuid", nullable: true),
                    LastCode = table.Column<int>(type: "integer", nullable: true),
                    LastVerdictKey = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    AcceptedVersionId = table.Column<Guid>(type: "uuid", nullable: true),
                    AcceptedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    AcceptedBy = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    AcceptedNote = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    Created = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_certificate_sensors", x => x.SensorId);
                    table.ForeignKey(
                        name: "FK_certificate_sensors_certificate_versions_AcceptedVersionId",
                        column: x => x.AcceptedVersionId,
                        principalTable: "certificate_versions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_certificate_sensors_certificate_versions_AnnouncedVersionId",
                        column: x => x.AnnouncedVersionId,
                        principalTable: "certificate_versions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_certificate_sensors_certificate_versions_CurrentVersionId",
                        column: x => x.CurrentVersionId,
                        principalTable: "certificate_versions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_certificate_sensors_sensors_SensorId",
                        column: x => x.SensorId,
                        principalTable: "sensors",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "certificate_sensor_authorities",
                columns: table => new
                {
                    SensorId = table.Column<Guid>(type: "uuid", nullable: false),
                    AuthorityId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_certificate_sensor_authorities", x => new { x.SensorId, x.AuthorityId });
                    table.ForeignKey(
                        name: "FK_certificate_sensor_authorities_certificate_authorities_Auth~",
                        column: x => x.AuthorityId,
                        principalTable: "certificate_authorities",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_certificate_sensor_authorities_certificate_sensors_SensorId",
                        column: x => x.SensorId,
                        principalTable: "certificate_sensors",
                        principalColumn: "SensorId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_certificate_authorities_Name",
                table: "certificate_authorities",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_certificate_sensor_authorities_AuthorityId",
                table: "certificate_sensor_authorities",
                column: "AuthorityId");

            migrationBuilder.CreateIndex(
                name: "IX_certificate_sensors_AcceptedVersionId",
                table: "certificate_sensors",
                column: "AcceptedVersionId");

            migrationBuilder.CreateIndex(
                name: "IX_certificate_sensors_AnnouncedVersionId",
                table: "certificate_sensors",
                column: "AnnouncedVersionId");

            migrationBuilder.CreateIndex(
                name: "IX_certificate_sensors_CurrentVersionId",
                table: "certificate_sensors",
                column: "CurrentVersionId");

            migrationBuilder.CreateIndex(
                name: "IX_certificate_sensors_NextCheckAt",
                table: "certificate_sensors",
                column: "NextCheckAt");

            migrationBuilder.CreateIndex(
                name: "IX_certificate_versions_SensorId_LastSeenAt",
                table: "certificate_versions",
                columns: new[] { "SensorId", "LastSeenAt" });

            migrationBuilder.CreateIndex(
                name: "IX_certificate_versions_SensorId_Sha256",
                table: "certificate_versions",
                columns: new[] { "SensorId", "Sha256" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "certificate_sensor_authorities");

            migrationBuilder.DropTable(
                name: "certificate_authorities");

            migrationBuilder.DropTable(
                name: "certificate_sensors");

            migrationBuilder.DropTable(
                name: "certificate_versions");
        }
    }
}
