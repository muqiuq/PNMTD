using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PNMTD.Data.Migrations
{
    /// <inheritdoc />
    public partial class NotificationSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "notification_settings",
                columns: table => new
                {
                    SingletonId = table.Column<int>(type: "integer", nullable: false),
                    PushoverLow = table.Column<int>(type: "integer", nullable: false),
                    PushoverNormal = table.Column<int>(type: "integer", nullable: false),
                    PushoverHigh = table.Column<int>(type: "integer", nullable: false),
                    EmergencyRetrySeconds = table.Column<int>(type: "integer", nullable: false),
                    EmergencyExpireSeconds = table.Column<int>(type: "integer", nullable: false),
                    ChangedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ChangedBy = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_notification_settings", x => x.SingletonId);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "notification_settings");
        }
    }
}
