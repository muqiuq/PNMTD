using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PNMTD.Data.Migrations
{
    /// <summary>
    /// A rule's recipient kind, user, group, channels, hours and last delivery (docs/notification-recipients-plan.md §3.1). Every new
    /// column has a default: existing rows stay address rules reached by both channels, and a v0.12 import needs no source column.
    /// </summary>
    public partial class NotificationRecipients : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "Recipient",
                table: "notificationrules",
                type: "text",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "text");

            migrationBuilder.AddColumn<bool>(
                name: "ByEmail",
                table: "notificationrules",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<bool>(
                name: "ByPushover",
                table: "notificationrules",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<Guid>(
                name: "GroupId",
                table: "notificationrules",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<short>(
                name: "Hours",
                table: "notificationrules",
                type: "smallint",
                nullable: false,
                defaultValue: (short)0);

            migrationBuilder.AddColumn<DateTime>(
                name: "LastSentAt",
                table: "notificationrules",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LastSentTo",
                table: "notificationrules",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<short>(
                name: "RecipientKind",
                table: "notificationrules",
                type: "smallint",
                nullable: false,
                defaultValue: (short)0);

            migrationBuilder.AddColumn<Guid>(
                name: "UserId",
                table: "notificationrules",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_notificationrules_GroupId",
                table: "notificationrules",
                column: "GroupId");

            migrationBuilder.CreateIndex(
                name: "IX_notificationrules_UserId",
                table: "notificationrules",
                column: "UserId");

            migrationBuilder.AddForeignKey(
                name: "FK_notificationrules_groups_GroupId",
                table: "notificationrules",
                column: "GroupId",
                principalTable: "groups",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_notificationrules_users_UserId",
                table: "notificationrules",
                column: "UserId",
                principalTable: "users",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_notificationrules_groups_GroupId",
                table: "notificationrules");

            migrationBuilder.DropForeignKey(
                name: "FK_notificationrules_users_UserId",
                table: "notificationrules");

            migrationBuilder.DropIndex(
                name: "IX_notificationrules_GroupId",
                table: "notificationrules");

            migrationBuilder.DropIndex(
                name: "IX_notificationrules_UserId",
                table: "notificationrules");

            migrationBuilder.DropColumn(
                name: "ByEmail",
                table: "notificationrules");

            migrationBuilder.DropColumn(
                name: "ByPushover",
                table: "notificationrules");

            migrationBuilder.DropColumn(
                name: "GroupId",
                table: "notificationrules");

            migrationBuilder.DropColumn(
                name: "Hours",
                table: "notificationrules");

            migrationBuilder.DropColumn(
                name: "LastSentAt",
                table: "notificationrules");

            migrationBuilder.DropColumn(
                name: "LastSentTo",
                table: "notificationrules");

            migrationBuilder.DropColumn(
                name: "RecipientKind",
                table: "notificationrules");

            migrationBuilder.DropColumn(
                name: "UserId",
                table: "notificationrules");

            migrationBuilder.AlterColumn<string>(
                name: "Recipient",
                table: "notificationrules",
                type: "text",
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "text",
                oldNullable: true);
        }
    }
}
