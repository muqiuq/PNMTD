using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PNMTD.Data.Migrations
{
    /// <inheritdoc />
    /// <summary>
    /// docs/responsibilities-plan.md §6, §10.3 (P3): the Redmine projects and trackers each responsibility lists, with the user who
    /// handles them (a deleted user leaves nobody); once per project and tracker, every tracker (null) included.
    /// </summary>
    public partial class ResponsibilityRedmine : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "responsibility_redmine",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ResponsibilityId = table.Column<Guid>(type: "uuid", nullable: false),
                    ProjectId = table.Column<int>(type: "integer", nullable: false),
                    ProjectName = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    TrackerId = table.Column<int>(type: "integer", nullable: true),
                    TrackerName = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    UserId = table.Column<Guid>(type: "uuid", nullable: true),
                    Note = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedByName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedBy = table.Column<Guid>(type: "uuid", nullable: true),
                    UpdatedByName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_responsibility_redmine", x => x.Id);
                    table.ForeignKey(
                        name: "FK_responsibility_redmine_responsibilities_ResponsibilityId",
                        column: x => x.ResponsibilityId,
                        principalTable: "responsibilities",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_responsibility_redmine_users_UserId",
                        column: x => x.UserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "IX_responsibility_redmine_ProjectId_TrackerId",
                table: "responsibility_redmine",
                columns: new[] { "ProjectId", "TrackerId" },
                unique: true)
                .Annotation("Npgsql:NullsDistinct", false);

            migrationBuilder.CreateIndex(
                name: "IX_responsibility_redmine_ResponsibilityId",
                table: "responsibility_redmine",
                column: "ResponsibilityId");

            migrationBuilder.CreateIndex(
                name: "IX_responsibility_redmine_UserId",
                table: "responsibility_redmine",
                column: "UserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "responsibility_redmine");
        }
    }
}
