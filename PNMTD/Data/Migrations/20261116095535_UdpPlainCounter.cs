using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PNMTD.Data.Migrations
{
    /// <inheritdoc />
    public partial class UdpPlainCounter : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "PlainFcntHigh",
                table: "udp_nodes",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "PlainFcntWindow",
                table: "udp_nodes",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "PlainFcntHigh",
                table: "udp_nodes");

            migrationBuilder.DropColumn(
                name: "PlainFcntWindow",
                table: "udp_nodes");
        }
    }
}
