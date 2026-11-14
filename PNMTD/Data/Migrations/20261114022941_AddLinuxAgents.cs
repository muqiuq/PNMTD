using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PNMTD.Data.Migrations
{
    /// <summary>
    /// L3 and L4 of docs/linux-sensor-plan.md (§9.3): the rights from each hello (agents.Rights, jsonb), the Linux switches
    /// (KeyAddedEvents on, SecurityUpdateEvents off, SecurityUpdateDays 7, as for a new server, existing rows included), when
    /// security updates were first seen pending and how many, the kernel and distribution for the fleet; and in agent_logins the
    /// key of a key login (indexed), whom sudo or su became, sudo's command and the names an "invalid user" source tried.
    /// </summary>
    public partial class AddLinuxAgents : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Distribution",
                table: "agents",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "KernelRelease",
                table: "agents",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "KeyAddedEvents",
                table: "agents",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<string>(
                name: "Rights",
                table: "agents",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SecurityPackages",
                table: "agents",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "SecurityUpdateDays",
                table: "agents",
                type: "integer",
                nullable: false,
                defaultValue: 7);

            migrationBuilder.AddColumn<bool>(
                name: "SecurityUpdateEvents",
                table: "agents",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "SecurityUpdates",
                table: "agents",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTime>(
                name: "SecurityUpdatesSince",
                table: "agents",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Command",
                table: "agent_logins",
                type: "character varying(2000)",
                maxLength: 2000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "KeyFingerprint",
                table: "agent_logins",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TargetUser",
                table: "agent_logins",
                type: "character varying(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<List<string>>(
                name: "TriedUsers",
                table: "agent_logins",
                type: "text[]",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_agent_logins_KeyFingerprint",
                table: "agent_logins",
                column: "KeyFingerprint");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_agent_logins_KeyFingerprint",
                table: "agent_logins");

            migrationBuilder.DropColumn(
                name: "Distribution",
                table: "agents");

            migrationBuilder.DropColumn(
                name: "KernelRelease",
                table: "agents");

            migrationBuilder.DropColumn(
                name: "KeyAddedEvents",
                table: "agents");

            migrationBuilder.DropColumn(
                name: "Rights",
                table: "agents");

            migrationBuilder.DropColumn(
                name: "SecurityPackages",
                table: "agents");

            migrationBuilder.DropColumn(
                name: "SecurityUpdateDays",
                table: "agents");

            migrationBuilder.DropColumn(
                name: "SecurityUpdateEvents",
                table: "agents");

            migrationBuilder.DropColumn(
                name: "SecurityUpdates",
                table: "agents");

            migrationBuilder.DropColumn(
                name: "SecurityUpdatesSince",
                table: "agents");

            migrationBuilder.DropColumn(
                name: "Command",
                table: "agent_logins");

            migrationBuilder.DropColumn(
                name: "KeyFingerprint",
                table: "agent_logins");

            migrationBuilder.DropColumn(
                name: "TargetUser",
                table: "agent_logins");

            migrationBuilder.DropColumn(
                name: "TriedUsers",
                table: "agent_logins");
        }
    }
}
