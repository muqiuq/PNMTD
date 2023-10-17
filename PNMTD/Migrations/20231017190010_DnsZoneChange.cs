using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PNMTD.Migrations
{
    /// <inheritdoc />
    public partial class DnsZoneChange : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "Type",
                table: "dnszoneentries",
                newName: "Name");

            migrationBuilder.AddColumn<int>(
                name: "RecordType",
                table: "dnszoneentries",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "RecordType",
                table: "dnszoneentries");

            migrationBuilder.RenameColumn(
                name: "Name",
                table: "dnszoneentries",
                newName: "Type");
        }
    }
}
