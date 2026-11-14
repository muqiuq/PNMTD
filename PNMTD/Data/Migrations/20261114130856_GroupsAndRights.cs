using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PNMTD.Data.Migrations
{
    /// <inheritdoc />
    public partial class GroupsAndRights : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsAdmin",
                table: "users",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "AdminOnly",
                table: "decisions",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "groups",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    NormalizedName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Description = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_groups", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "group_rights",
                columns: table => new
                {
                    GroupId = table.Column<Guid>(type: "uuid", nullable: false),
                    Area = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Level = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_group_rights", x => new { x.GroupId, x.Area });
                    table.ForeignKey(
                        name: "FK_group_rights_groups_GroupId",
                        column: x => x.GroupId,
                        principalTable: "groups",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "user_groups",
                columns: table => new
                {
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    GroupId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_user_groups", x => new { x.UserId, x.GroupId });
                    table.ForeignKey(
                        name: "FK_user_groups_groups_GroupId",
                        column: x => x.GroupId,
                        principalTable: "groups",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_user_groups_users_UserId",
                        column: x => x.UserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_groups_NormalizedName",
                table: "groups",
                column: "NormalizedName",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_user_groups_GroupId",
                table: "user_groups",
                column: "GroupId");

            // The roles become the admin flag and two ordinary groups (docs/audit-redmine-zabbix-plan.md §5.3): Admin the flag,
            // User the group Operators, Viewer the group Viewers, with exactly the rights each role had (and Redmine's and
            // Zabbix's, new). Fresh installations start with both groups as well.
            migrationBuilder.Sql("""
                INSERT INTO groups ("Id", "Name", "NormalizedName", "Description", "CreatedAt", "UpdatedAt") VALUES
                    ('6a7b0000-0000-4000-8000-000000000001', 'Operators', 'operators', 'What the role User could do until 1.4: edit monitoring, run commands, read mail, decide, edit locations and pages.', now(), now()),
                    ('6a7b0000-0000-4000-8000-000000000002', 'Viewers', 'viewers', 'What the role Viewer could do until 1.4: see monitoring, locations and pages.', now(), now());
                INSERT INTO group_rights ("GroupId", "Area", "Level") VALUES
                    ('6a7b0000-0000-4000-8000-000000000001', 'Monitoring', 'Edit'), ('6a7b0000-0000-4000-8000-000000000001', 'Commands', 'Run'), ('6a7b0000-0000-4000-8000-000000000001', 'Mail', 'Read'),
                    ('6a7b0000-0000-4000-8000-000000000001', 'Decisions', 'Decide'), ('6a7b0000-0000-4000-8000-000000000001', 'Locations', 'Edit'), ('6a7b0000-0000-4000-8000-000000000001', 'KnowledgeBase', 'Edit'),
                    ('6a7b0000-0000-4000-8000-000000000001', 'Redmine', 'Link'), ('6a7b0000-0000-4000-8000-000000000001', 'Zabbix', 'Details'),
                    ('6a7b0000-0000-4000-8000-000000000002', 'Monitoring', 'View'), ('6a7b0000-0000-4000-8000-000000000002', 'Locations', 'View'), ('6a7b0000-0000-4000-8000-000000000002', 'KnowledgeBase', 'View'),
                    ('6a7b0000-0000-4000-8000-000000000002', 'Zabbix', 'View');
                UPDATE users SET "IsAdmin" = TRUE WHERE "Role" = 'Admin';
                INSERT INTO user_groups ("UserId", "GroupId") SELECT "Id", '6a7b0000-0000-4000-8000-000000000001' FROM users WHERE "Role" = 'User';
                INSERT INTO user_groups ("UserId", "GroupId") SELECT "Id", '6a7b0000-0000-4000-8000-000000000002' FROM users WHERE "Role" = 'Viewer';
                UPDATE decisions SET "AdminOnly" = TRUE WHERE "RequiredRole" = 'Admin';
                """);

            migrationBuilder.DropColumn(
                name: "Role",
                table: "users");

            migrationBuilder.DropColumn(
                name: "RequiredRole",
                table: "decisions");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(name: "Role", table: "users", type: "text", nullable: false, defaultValue: "Viewer");
            migrationBuilder.AddColumn<string>(name: "RequiredRole", table: "decisions", type: "character varying(16)", maxLength: 16,
                nullable: false, defaultValue: "User");
            // Back to roles: the flag is Admin, a member of a group with Monitoring's Edit a User, everyone else a Viewer.
            migrationBuilder.Sql("""
                UPDATE users SET "Role" = 'User' WHERE "Id" IN (SELECT m."UserId" FROM user_groups m JOIN group_rights r ON r."GroupId" = m."GroupId"
                    WHERE r."Area" = 'Monitoring' AND r."Level" = 'Edit');
                UPDATE users SET "Role" = 'Admin' WHERE "IsAdmin";
                UPDATE decisions SET "RequiredRole" = 'Admin' WHERE "AdminOnly";
                """);
            migrationBuilder.DropTable(name: "group_rights");
            migrationBuilder.DropTable(name: "user_groups");
            migrationBuilder.DropTable(name: "groups");
            migrationBuilder.DropColumn(name: "IsAdmin", table: "users");
            migrationBuilder.DropColumn(name: "AdminOnly", table: "decisions");
        }
    }
}
