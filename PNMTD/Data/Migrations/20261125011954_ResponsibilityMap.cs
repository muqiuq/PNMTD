using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PNMTD.Data.Migrations
{
    /// <summary>The responsibilities' places on the overview, saved by hand for everyone (docs/responsibilities-extensions-plan.md §7.2).</summary>
    public partial class ResponsibilityMap : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "responsibility_map_positions",
                columns: table => new
                {
                    ResponsibilityId = table.Column<Guid>(type: "uuid", nullable: false),
                    X = table.Column<double>(type: "double precision", nullable: false),
                    Y = table.Column<double>(type: "double precision", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedBy = table.Column<Guid>(type: "uuid", nullable: true),
                    UpdatedByName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_responsibility_map_positions", x => x.ResponsibilityId);
                    table.ForeignKey(
                        name: "FK_responsibility_map_positions_responsibilities_Responsibilit~",
                        column: x => x.ResponsibilityId,
                        principalTable: "responsibilities",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "responsibility_map_positions");
        }
    }
}
