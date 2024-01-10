﻿using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PNMTD.Migrations
{
    /// <inheritdoc />
    public partial class NotificationRuleReAddConstraint : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_notificationruleevent_events_EventId",
                table: "notificationruleevent");

            migrationBuilder.AlterColumn<Guid>(
                name: "EventId",
                table: "notificationruleevent",
                type: "TEXT",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "TEXT",
                oldNullable: true);

            migrationBuilder.AddForeignKey(
                name: "FK_notificationruleevent_events_EventId",
                table: "notificationruleevent",
                column: "EventId",
                principalTable: "events",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_notificationruleevent_events_EventId",
                table: "notificationruleevent");

            migrationBuilder.AlterColumn<Guid>(
                name: "EventId",
                table: "notificationruleevent",
                type: "TEXT",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "TEXT");

            migrationBuilder.AddForeignKey(
                name: "FK_notificationruleevent_events_EventId",
                table: "notificationruleevent",
                column: "EventId",
                principalTable: "events",
                principalColumn: "Id");
        }
    }
}
