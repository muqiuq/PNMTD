using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PNMTD.Migrations
{
    /// <inheritdoc />
    public partial class MailLog : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "maillogs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Processed = table.Column<bool>(type: "INTEGER", nullable: false),
                    ProcessedById = table.Column<Guid>(type: "TEXT", nullable: true),
                    ProcessLog = table.Column<string>(type: "TEXT", nullable: false),
                    Created = table.Column<DateTime>(type: "TEXT", nullable: false),
                    From = table.Column<string>(type: "TEXT", nullable: false),
                    To = table.Column<string>(type: "TEXT", nullable: false),
                    Subject = table.Column<string>(type: "TEXT", nullable: false),
                    Content = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_maillogs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_maillogs_mailinputs_ProcessedById",
                        column: x => x.ProcessedById,
                        principalTable: "mailinputs",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateIndex(
                name: "IX_maillogs_ProcessedById",
                table: "maillogs",
                column: "ProcessedById");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "maillogs");
        }
    }
}
