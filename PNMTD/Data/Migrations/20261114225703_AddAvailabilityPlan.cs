using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PNMTD.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddAvailabilityPlan : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Color",
                table: "users",
                type: "character varying(7)",
                maxLength: 7,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DefaultWeek",
                table: "users",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Email",
                table: "users",
                type: "character varying(254)",
                maxLength: 254,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "InPlan",
                table: "users",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "PlanOrder",
                table: "users",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "PushoverKey",
                table: "users",
                type: "character varying(30)",
                maxLength: 30,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RedmineName",
                table: "users",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ShortCode",
                table: "users",
                type: "character varying(3)",
                maxLength: 3,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "StopComment",
                table: "cookie_cases",
                type: "character varying(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "StoppedAt",
                table: "cookie_cases",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "StoppedById",
                table: "cookie_cases",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "StoppedByName",
                table: "cookie_cases",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "availability_days",
                columns: table => new
                {
                    Date = table.Column<DateOnly>(type: "date", nullable: false),
                    ResponsibleId = table.Column<Guid>(type: "uuid", nullable: true),
                    FallbackId = table.Column<Guid>(type: "uuid", nullable: true),
                    Comment = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    ChangedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ChangedBy = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_availability_days", x => x.Date);
                    table.ForeignKey(
                        name: "FK_availability_days_users_FallbackId",
                        column: x => x.FallbackId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_availability_days_users_ResponsibleId",
                        column: x => x.ResponsibleId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "availability_settings",
                columns: table => new
                {
                    SingletonId = table.Column<int>(type: "integer", nullable: false),
                    Json = table.Column<string>(type: "text", nullable: false),
                    ChangedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ChangedBy = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_availability_settings", x => x.SingletonId);
                });

            migrationBuilder.CreateTable(
                name: "availability_types",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    Code = table.Column<string>(type: "character varying(4)", maxLength: 4, nullable: false),
                    Color = table.Column<string>(type: "character varying(7)", maxLength: 7, nullable: false),
                    Vacation = table.Column<bool>(type: "boolean", nullable: false),
                    Holiday = table.Column<bool>(type: "boolean", nullable: false),
                    OnSite = table.Column<bool>(type: "boolean", nullable: false),
                    WorkTravel = table.Column<bool>(type: "boolean", nullable: false),
                    HomeOffice = table.Column<bool>(type: "boolean", nullable: false),
                    Compensation = table.Column<bool>(type: "boolean", nullable: false),
                    IsDefault = table.Column<bool>(type: "boolean", nullable: false),
                    IsDayOff = table.Column<bool>(type: "boolean", nullable: false),
                    Order = table.Column<int>(type: "integer", nullable: false),
                    Retired = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_availability_types", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "availability_entries",
                columns: table => new
                {
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Date = table.Column<DateOnly>(type: "date", nullable: false),
                    TypeId = table.Column<Guid>(type: "uuid", nullable: false),
                    Filled = table.Column<bool>(type: "boolean", nullable: false),
                    SetAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    SetBy = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_availability_entries", x => new { x.UserId, x.Date });
                    table.ForeignKey(
                        name: "FK_availability_entries_availability_types_TypeId",
                        column: x => x.TypeId,
                        principalTable: "availability_types",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_availability_entries_users_UserId",
                        column: x => x.UserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_users_ShortCode",
                table: "users",
                column: "ShortCode",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_availability_days_FallbackId",
                table: "availability_days",
                column: "FallbackId");

            migrationBuilder.CreateIndex(
                name: "IX_availability_days_ResponsibleId",
                table: "availability_days",
                column: "ResponsibleId");

            migrationBuilder.CreateIndex(
                name: "IX_availability_entries_Date",
                table: "availability_entries",
                column: "Date");

            migrationBuilder.CreateIndex(
                name: "IX_availability_entries_TypeId",
                table: "availability_entries",
                column: "TypeId");

            // Revision 7 (docs/cookie-plan.md §20.1): the types of the team's Excel plan, ANWE the default and UNAV for days off;
            // Cookie's contacts move to their users, its working hours and holidays to the plan's settings, and the two
            // sections go. The recipient rules and quiet hours are dropped (1.5 isn't released).
            migrationBuilder.Sql("""
                INSERT INTO availability_types ("Id", "Name", "Code", "Color", "Vacation", "Holiday", "OnSite", "WorkTravel", "HomeOffice",
                    "Compensation", "IsDefault", "IsDayOff", "Order", "Retired") VALUES
                ('a7a11ab1-0000-4000-8000-000000000001', 'Present', 'ANWE', '#c6efce', false, false, true, false, false, false, true, false, 0, false),
                ('a7a11ab1-0000-4000-8000-000000000002', 'Home office', 'HOME', '#ffeb9c', false, false, false, false, true, false, false, false, 1, false),
                ('a7a11ab1-0000-4000-8000-000000000003', 'Work travel', 'EXTN', '#fcd5b4', false, false, false, true, false, false, false, false, 2, false),
                ('a7a11ab1-0000-4000-8000-000000000004', 'Vacation', 'VACA', '#8ea9db', true, false, false, false, false, false, false, false, 3, false),
                ('a7a11ab1-0000-4000-8000-000000000005', 'Compensation', 'KOMP', '#595959', false, false, false, false, false, true, false, false, 4, false),
                ('a7a11ab1-0000-4000-8000-000000000006', 'Unavailable', 'UNAV', '#8496b0', false, false, false, false, false, false, false, true, 5, false);
                """);
            migrationBuilder.Sql("""
                UPDATE users u SET "Email" = NULLIF(left(x.c->>'email', 254), ''), "PushoverKey" = NULLIF(left(x.c->>'pushoverKey', 30), ''),
                    "RedmineName" = NULLIF(left(x.c->>'redmineName', 200), '')
                FROM (SELECT jsonb_array_elements(COALESCE(s."Json"::jsonb->'contacts', '[]'::jsonb)) AS c FROM cookie_settings s
                      WHERE s."Section" = 'notifications'
                        AND s."Version" = (SELECT max("Version") FROM cookie_settings WHERE "Section" = 'notifications')) x
                WHERE u."Id" = (x.c->>'userId')::uuid;
                """);
            migrationBuilder.Sql("""
                INSERT INTO availability_settings ("SingletonId", "Json", "ChangedAt", "ChangedBy")
                SELECT 1, jsonb_build_object('week', h.j->'week', 'holidays', COALESCE(h.j->'holidays', '[]'::jsonb))::text, now(), 'migration'
                FROM (SELECT "Json"::jsonb AS j FROM cookie_settings WHERE "Section" = 'hours' ORDER BY "Version" DESC LIMIT 1) h
                WHERE h.j ? 'week';
                DELETE FROM cookie_settings WHERE "Section" IN ('hours', 'notifications');
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "availability_days");

            migrationBuilder.DropTable(
                name: "availability_entries");

            migrationBuilder.DropTable(
                name: "availability_settings");

            migrationBuilder.DropTable(
                name: "availability_types");

            migrationBuilder.DropIndex(
                name: "IX_users_ShortCode",
                table: "users");

            migrationBuilder.DropColumn(
                name: "Color",
                table: "users");

            migrationBuilder.DropColumn(
                name: "DefaultWeek",
                table: "users");

            migrationBuilder.DropColumn(
                name: "Email",
                table: "users");

            migrationBuilder.DropColumn(
                name: "InPlan",
                table: "users");

            migrationBuilder.DropColumn(
                name: "PlanOrder",
                table: "users");

            migrationBuilder.DropColumn(
                name: "PushoverKey",
                table: "users");

            migrationBuilder.DropColumn(
                name: "RedmineName",
                table: "users");

            migrationBuilder.DropColumn(
                name: "ShortCode",
                table: "users");

            migrationBuilder.DropColumn(
                name: "StopComment",
                table: "cookie_cases");

            migrationBuilder.DropColumn(
                name: "StoppedAt",
                table: "cookie_cases");

            migrationBuilder.DropColumn(
                name: "StoppedById",
                table: "cookie_cases");

            migrationBuilder.DropColumn(
                name: "StoppedByName",
                table: "cookie_cases");
        }
    }
}
