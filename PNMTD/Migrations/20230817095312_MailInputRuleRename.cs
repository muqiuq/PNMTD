using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PNMTD.Migrations
{
    /// <inheritdoc />
    public partial class MailInputRuleRename : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_maillogs_mailinputs_ProcessedById",
                table: "maillogs");

            migrationBuilder.DropTable(
                name: "mailinputs");

            migrationBuilder.CreateTable(
                name: "mailinputrules",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    BodyTest = table.Column<string>(type: "TEXT", nullable: true),
                    FromTest = table.Column<string>(type: "TEXT", nullable: true),
                    SubjectTest = table.Column<string>(type: "TEXT", nullable: true),
                    OkCode = table.Column<int>(type: "INTEGER", nullable: true),
                    OkTest = table.Column<string>(type: "TEXT", nullable: true),
                    FailCode = table.Column<int>(type: "INTEGER", nullable: true),
                    FailTest = table.Column<string>(type: "TEXT", nullable: true),
                    DefaultCode = table.Column<int>(type: "INTEGER", nullable: false),
                    SensorOutputId = table.Column<Guid>(type: "TEXT", nullable: true)
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

            migrationBuilder.CreateIndex(
                name: "IX_mailinputrules_SensorOutputId",
                table: "mailinputrules",
                column: "SensorOutputId");

            migrationBuilder.AddForeignKey(
                name: "FK_maillogs_mailinputrules_ProcessedById",
                table: "maillogs",
                column: "ProcessedById",
                principalTable: "mailinputrules",
                principalColumn: "Id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_maillogs_mailinputrules_ProcessedById",
                table: "maillogs");

            migrationBuilder.DropTable(
                name: "mailinputrules");

            migrationBuilder.CreateTable(
                name: "mailinputs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    SensorOutputId = table.Column<Guid>(type: "TEXT", nullable: true),
                    ContentTest = table.Column<string>(type: "TEXT", nullable: true),
                    DefaultCode = table.Column<int>(type: "INTEGER", nullable: false),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    FailCode = table.Column<int>(type: "INTEGER", nullable: true),
                    FailTest = table.Column<string>(type: "TEXT", nullable: true),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    OkCode = table.Column<int>(type: "INTEGER", nullable: true),
                    OkTest = table.Column<string>(type: "TEXT", nullable: true),
                    SenderTest = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_mailinputs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_mailinputs_sensors_SensorOutputId",
                        column: x => x.SensorOutputId,
                        principalTable: "sensors",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateIndex(
                name: "IX_mailinputs_SensorOutputId",
                table: "mailinputs",
                column: "SensorOutputId");

            migrationBuilder.AddForeignKey(
                name: "FK_maillogs_mailinputs_ProcessedById",
                table: "maillogs",
                column: "ProcessedById",
                principalTable: "mailinputs",
                principalColumn: "Id");
        }
    }
}
