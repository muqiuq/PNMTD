using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PNMTD.Migrations
{
    /// <inheritdoc />
    public partial class KeyValue : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "keyvalues",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    Note = table.Column<string>(type: "TEXT", nullable: false),
                    Key = table.Column<string>(type: "TEXT", nullable: false),
                    Value = table.Column<string>(type: "TEXT", nullable: false),
                    IsReadOnly = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_keyvalues", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_keyvalues_Key",
                table: "keyvalues",
                column: "Key",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "keyvalues");
        }
    }
}
