using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PNMTD.Migrations
{
    /// <inheritdoc />
    public partial class MailInputEntity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "mailinputs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    ContentTest = table.Column<string>(type: "TEXT", nullable: true),
                    SenderTest = table.Column<string>(type: "TEXT", nullable: true),
                    OkCode = table.Column<int>(type: "INTEGER", nullable: true),
                    OkTest = table.Column<string>(type: "TEXT", nullable: true),
                    FailCode = table.Column<int>(type: "INTEGER", nullable: true),
                    FailTest = table.Column<string>(type: "TEXT", nullable: true),
                    DefaultCode = table.Column<int>(type: "INTEGER", nullable: false),
                    SensorOutputId = table.Column<Guid>(type: "TEXT", nullable: true)
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
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "mailinputs");
        }
    }
}
