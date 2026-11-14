using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PNMTD.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddLocations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "LocationId",
                table: "hosts",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "location_fields",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Key = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Label = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Type = table.Column<int>(type: "integer", nullable: false),
                    Help = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    Group = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    Position = table.Column<int>(type: "integer", nullable: false),
                    Overview = table.Column<bool>(type: "boolean", nullable: false),
                    Inherited = table.Column<bool>(type: "boolean", nullable: false),
                    Sensitive = table.Column<bool>(type: "boolean", nullable: false),
                    Options = table.Column<string>(type: "jsonb", nullable: true),
                    ArchivedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_location_fields", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "locations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ParentId = table.Column<Guid>(type: "uuid", nullable: true),
                    Name = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    NormalizedName = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Description = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedByName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedBy = table.Column<Guid>(type: "uuid", nullable: true),
                    UpdatedByName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_locations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_locations_locations_ParentId",
                        column: x => x.ParentId,
                        principalTable: "locations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "location_history",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    LocationId = table.Column<Guid>(type: "uuid", nullable: false),
                    At = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    ByName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Kind = table.Column<int>(type: "integer", nullable: false),
                    FieldId = table.Column<Guid>(type: "uuid", nullable: true),
                    OldValue = table.Column<string>(type: "jsonb", nullable: true),
                    NewValue = table.Column<string>(type: "jsonb", nullable: true),
                    Detail = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_location_history", x => x.Id);
                    table.ForeignKey(
                        name: "FK_location_history_location_fields_FieldId",
                        column: x => x.FieldId,
                        principalTable: "location_fields",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_location_history_locations_LocationId",
                        column: x => x.LocationId,
                        principalTable: "locations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "location_values",
                columns: table => new
                {
                    LocationId = table.Column<Guid>(type: "uuid", nullable: false),
                    FieldId = table.Column<Guid>(type: "uuid", nullable: false),
                    Value = table.Column<string>(type: "jsonb", nullable: true),
                    Revision = table.Column<int>(type: "integer", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedBy = table.Column<Guid>(type: "uuid", nullable: true),
                    UpdatedByName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_location_values", x => new { x.LocationId, x.FieldId });
                    table.ForeignKey(
                        name: "FK_location_values_location_fields_FieldId",
                        column: x => x.FieldId,
                        principalTable: "location_fields",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_location_values_locations_LocationId",
                        column: x => x.LocationId,
                        principalTable: "locations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.InsertData(
                table: "location_fields",
                columns: new[] { "Id", "ArchivedAt", "CreatedAt", "Group", "Help", "Inherited", "Key", "Label", "Options", "Overview", "Position", "Sensitive", "Type" },
                values: new object[] { new Guid("6f1c0d7e-2b4a-4c8e-9a35-0e7b5d1f4a01"), null, new DateTime(2026, 11, 14, 0, 0, 0, 0, DateTimeKind.Utc), null, null, false, "overview", "Overview", null, true, 0, false, 0 });

            migrationBuilder.CreateIndex(
                name: "IX_hosts_LocationId",
                table: "hosts",
                column: "LocationId");

            migrationBuilder.CreateIndex(
                name: "IX_location_fields_Key",
                table: "location_fields",
                column: "Key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_location_history_FieldId",
                table: "location_history",
                column: "FieldId");

            migrationBuilder.CreateIndex(
                name: "IX_location_history_LocationId_At",
                table: "location_history",
                columns: new[] { "LocationId", "At" });

            migrationBuilder.CreateIndex(
                name: "IX_location_values_FieldId",
                table: "location_values",
                column: "FieldId");

            migrationBuilder.CreateIndex(
                name: "IX_locations_ParentId_NormalizedName",
                table: "locations",
                columns: new[] { "ParentId", "NormalizedName" },
                unique: true)
                .Annotation("Npgsql:NullsDistinct", false);

            migrationBuilder.AddForeignKey(
                name: "FK_hosts_locations_LocationId",
                table: "hosts",
                column: "LocationId",
                principalTable: "locations",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_hosts_locations_LocationId",
                table: "hosts");

            migrationBuilder.DropTable(
                name: "location_history");

            migrationBuilder.DropTable(
                name: "location_values");

            migrationBuilder.DropTable(
                name: "location_fields");

            migrationBuilder.DropTable(
                name: "locations");

            migrationBuilder.DropIndex(
                name: "IX_hosts_LocationId",
                table: "hosts");

            migrationBuilder.DropColumn(
                name: "LocationId",
                table: "hosts");
        }
    }
}
