using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace PNMTD.Data.Migrations
{
    /// <inheritdoc />
    public partial class CookieObjectNotes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "cookie_object_notes",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    HostId = table.Column<Guid>(type: "uuid", nullable: true),
                    SensorId = table.Column<Guid>(type: "uuid", nullable: true),
                    At = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Text = table.Column<string>(type: "character varying(1500)", maxLength: 1500, nullable: false),
                    CaseId = table.Column<Guid>(type: "uuid", nullable: true),
                    DecisionId = table.Column<long>(type: "bigint", nullable: true),
                    Areas = table.Column<List<string>>(type: "text[]", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_cookie_object_notes", x => x.Id);
                    table.CheckConstraint("CK_cookie_object_notes_object", "(\"HostId\" IS NULL) <> (\"SensorId\" IS NULL)");
                    table.ForeignKey(
                        name: "FK_cookie_object_notes_cookie_cases_CaseId",
                        column: x => x.CaseId,
                        principalTable: "cookie_cases",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_cookie_object_notes_cookie_decisions_DecisionId",
                        column: x => x.DecisionId,
                        principalTable: "cookie_decisions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_cookie_object_notes_hosts_HostId",
                        column: x => x.HostId,
                        principalTable: "hosts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_cookie_object_notes_sensors_SensorId",
                        column: x => x.SensorId,
                        principalTable: "sensors",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_cookie_object_notes_CaseId",
                table: "cookie_object_notes",
                column: "CaseId");

            migrationBuilder.CreateIndex(
                name: "IX_cookie_object_notes_DecisionId",
                table: "cookie_object_notes",
                column: "DecisionId");

            migrationBuilder.CreateIndex(
                name: "IX_cookie_object_notes_HostId_At",
                table: "cookie_object_notes",
                columns: new[] { "HostId", "At" });

            migrationBuilder.CreateIndex(
                name: "IX_cookie_object_notes_SensorId_At",
                table: "cookie_object_notes",
                columns: new[] { "SensorId", "At" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "cookie_object_notes");
        }
    }
}
