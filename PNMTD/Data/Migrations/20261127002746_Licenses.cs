using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace PNMTD.Data.Migrations
{
    /// <inheritdoc />
    public partial class Licenses : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_file_uses_one_owner",
                table: "file_uses");

            migrationBuilder.AddColumn<Guid>(
                name: "LicenseId",
                table: "file_uses",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "license_categories",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    NormalizedName = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    Description = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    SortOrder = table.Column<int>(type: "integer", nullable: false),
                    DefaultFor = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_license_categories", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "license_templates",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    NormalizedName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Type = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    CategoryId = table.Column<Guid>(type: "uuid", nullable: false),
                    Manufacturer = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    ProductName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Edition = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    Vendor = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    ProductUrl = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    Kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    Seats = table.Column<int>(type: "integer", nullable: true),
                    SeatUnit = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: true),
                    SeatsFromHosts = table.Column<bool>(type: "boolean", nullable: true),
                    PriceAmount = table.Column<decimal>(type: "numeric(12,2)", nullable: true),
                    Currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: true),
                    PriceBasis = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    Billing = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    BillingMonths = table.Column<int>(type: "integer", nullable: true),
                    TermMonths = table.Column<int>(type: "integer", nullable: true),
                    Renewal = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    WarnDays = table.Column<int>(type: "integer", nullable: true),
                    NoticeDays = table.Column<int>(type: "integer", nullable: true),
                    WarnBeforeRenewal = table.Column<bool>(type: "boolean", nullable: true),
                    ReviewMonths = table.Column<int>(type: "integer", nullable: true),
                    SupportContact = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    SupportUrl = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    Sla = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    EndOfLifeOn = table.Column<DateOnly>(type: "date", nullable: true),
                    EndOfLifeNote = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    Note = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedBy = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_license_templates", x => x.Id);
                    table.ForeignKey(
                        name: "FK_license_templates_license_categories_CategoryId",
                        column: x => x.CategoryId,
                        principalTable: "license_categories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "licenses",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Number = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Type = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    TemplateId = table.Column<Guid>(type: "uuid", nullable: true),
                    CategoryId = table.Column<Guid>(type: "uuid", nullable: false),
                    Manufacturer = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    ProductName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    Edition = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    Vendor = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    Kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    LicensedTo = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    Reference = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    CustomerNumber = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    PortalUrl = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    SupportContact = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    SupportUrl = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    Sla = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    CostCenter = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    LocationId = table.Column<Guid>(type: "uuid", nullable: true),
                    OwnerId = table.Column<Guid>(type: "uuid", nullable: true),
                    LicenseKeyProtected = table.Column<string>(type: "text", nullable: true),
                    LicenseKeyHint = table.Column<string>(type: "character varying(5)", maxLength: 5, nullable: true),
                    ProductKeyProtected = table.Column<string>(type: "text", nullable: true),
                    ProductKeyHint = table.Column<string>(type: "character varying(5)", maxLength: 5, nullable: true),
                    Seats = table.Column<int>(type: "integer", nullable: true),
                    SeatUnit = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    SeatsFromHosts = table.Column<bool>(type: "boolean", nullable: false),
                    PriceAmount = table.Column<decimal>(type: "numeric(12,2)", nullable: true),
                    Currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    PriceBasis = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Billing = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    BillingMonths = table.Column<int>(type: "integer", nullable: true),
                    RecordCharges = table.Column<bool>(type: "boolean", nullable: false),
                    ChargesFrom = table.Column<DateOnly>(type: "date", nullable: true),
                    StartsOn = table.Column<DateOnly>(type: "date", nullable: true),
                    ExpiresOn = table.Column<DateOnly>(type: "date", nullable: true),
                    TermMonths = table.Column<int>(type: "integer", nullable: true),
                    Renewal = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    WarnDays = table.Column<int>(type: "integer", nullable: false),
                    NoticeDays = table.Column<int>(type: "integer", nullable: true),
                    WarnBeforeRenewal = table.Column<bool>(type: "boolean", nullable: false),
                    EndOfLifeFromTemplate = table.Column<bool>(type: "boolean", nullable: false),
                    EndOfLifeOn = table.Column<DateOnly>(type: "date", nullable: true),
                    EndOfLifeNote = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    EolWarnDays = table.Column<int>(type: "integer", nullable: false),
                    ReviewedOn = table.Column<DateOnly>(type: "date", nullable: true),
                    ReviewedBy = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    ReviewNote = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    ReviewMonths = table.Column<int>(type: "integer", nullable: true),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    StatusChangedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    StatusChangedBy = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    Note = table.Column<string>(type: "character varying(10000)", maxLength: 10000, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedBy = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    VerdictCode = table.Column<int>(type: "integer", nullable: true),
                    VerdictKey = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    VerdictMessage = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    VerdictAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    AnnouncedExpiresOn = table.Column<DateOnly>(type: "date", nullable: true),
                    NoticeKey = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    ReviewNoticeOn = table.Column<DateOnly>(type: "date", nullable: true),
                    AutoRenewedThrough = table.Column<DateOnly>(type: "date", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_licenses", x => x.Id);
                    table.ForeignKey(
                        name: "FK_licenses_license_categories_CategoryId",
                        column: x => x.CategoryId,
                        principalTable: "license_categories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_licenses_license_templates_TemplateId",
                        column: x => x.TemplateId,
                        principalTable: "license_templates",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_licenses_locations_LocationId",
                        column: x => x.LocationId,
                        principalTable: "locations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_licenses_users_OwnerId",
                        column: x => x.OwnerId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "license_assignments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    LicenseId = table.Column<Guid>(type: "uuid", nullable: false),
                    HostId = table.Column<Guid>(type: "uuid", nullable: false),
                    SensorId = table.Column<Guid>(type: "uuid", nullable: true),
                    Seats = table.Column<int>(type: "integer", nullable: false),
                    Note = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    AssignedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    AssignedBy = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    SensorKey = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_license_assignments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_license_assignments_hosts_HostId",
                        column: x => x.HostId,
                        principalTable: "hosts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_license_assignments_licenses_LicenseId",
                        column: x => x.LicenseId,
                        principalTable: "licenses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_license_assignments_sensors_SensorId",
                        column: x => x.SensorId,
                        principalTable: "sensors",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "license_charges",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    LicenseId = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Source = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    On = table.Column<DateOnly>(type: "date", nullable: false),
                    PeriodFrom = table.Column<DateOnly>(type: "date", nullable: true),
                    PeriodTo = table.Column<DateOnly>(type: "date", nullable: true),
                    Amount = table.Column<decimal>(type: "numeric(12,2)", nullable: false),
                    Currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    Estimated = table.Column<bool>(type: "boolean", nullable: false),
                    Reference = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    Note = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedBy = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    UpdatedBy = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_license_charges", x => x.Id);
                    table.ForeignKey(
                        name: "FK_license_charges_licenses_LicenseId",
                        column: x => x.LicenseId,
                        principalTable: "licenses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "license_renewals",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    LicenseId = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    On = table.Column<DateOnly>(type: "date", nullable: false),
                    PreviousExpiresOn = table.Column<DateOnly>(type: "date", nullable: true),
                    NewExpiresOn = table.Column<DateOnly>(type: "date", nullable: false),
                    PriceAmount = table.Column<decimal>(type: "numeric(12,2)", nullable: true),
                    Currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: true),
                    Reference = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    Note = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedBy = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_license_renewals", x => x.Id);
                    table.ForeignKey(
                        name: "FK_license_renewals_licenses_LicenseId",
                        column: x => x.LicenseId,
                        principalTable: "licenses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "license_sensor_links",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    LicenseId = table.Column<Guid>(type: "uuid", nullable: false),
                    SensorId = table.Column<Guid>(type: "uuid", nullable: false),
                    Note = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedBy = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_license_sensor_links", x => x.Id);
                    table.ForeignKey(
                        name: "FK_license_sensor_links_licenses_LicenseId",
                        column: x => x.LicenseId,
                        principalTable: "licenses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_license_sensor_links_sensors_SensorId",
                        column: x => x.SensorId,
                        principalTable: "sensors",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "license_documents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    LicenseId = table.Column<Guid>(type: "uuid", nullable: false),
                    FileId = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Description = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    ChargeId = table.Column<Guid>(type: "uuid", nullable: true),
                    RenewalId = table.Column<Guid>(type: "uuid", nullable: true),
                    AddedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    AddedBy = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_license_documents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_license_documents_files_FileId",
                        column: x => x.FileId,
                        principalTable: "files",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_license_documents_license_charges_ChargeId",
                        column: x => x.ChargeId,
                        principalTable: "license_charges",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_license_documents_license_renewals_RenewalId",
                        column: x => x.RenewalId,
                        principalTable: "license_renewals",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_license_documents_licenses_LicenseId",
                        column: x => x.LicenseId,
                        principalTable: "licenses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_file_uses_LicenseId_FileId",
                table: "file_uses",
                columns: new[] { "LicenseId", "FileId" },
                unique: true,
                filter: "\"LicenseId\" IS NOT NULL");

            migrationBuilder.AddCheckConstraint(
                name: "CK_file_uses_one_owner",
                table: "file_uses",
                sql: "num_nonnulls(\"PageId\", \"LocationId\", \"ResponsibilityId\", \"BoardId\", \"LicenseId\") = 1");

            migrationBuilder.CreateIndex(
                name: "IX_license_assignments_HostId",
                table: "license_assignments",
                column: "HostId");

            migrationBuilder.CreateIndex(
                name: "IX_license_assignments_LicenseId_HostId",
                table: "license_assignments",
                columns: new[] { "LicenseId", "HostId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_license_assignments_SensorId",
                table: "license_assignments",
                column: "SensorId",
                unique: true,
                filter: "\"SensorId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_license_categories_DefaultFor",
                table: "license_categories",
                column: "DefaultFor",
                unique: true,
                filter: "\"DefaultFor\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_license_categories_NormalizedName",
                table: "license_categories",
                column: "NormalizedName",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_license_charges_automatic_period",
                table: "license_charges",
                columns: new[] { "LicenseId", "PeriodFrom" },
                unique: true,
                filter: "\"Source\" = 'Automatic' AND \"Kind\" = 'Period'");

            migrationBuilder.CreateIndex(
                name: "IX_license_charges_LicenseId",
                table: "license_charges",
                column: "LicenseId");

            migrationBuilder.CreateIndex(
                name: "IX_license_charges_On",
                table: "license_charges",
                column: "On");

            migrationBuilder.CreateIndex(
                name: "IX_license_documents_ChargeId",
                table: "license_documents",
                column: "ChargeId");

            migrationBuilder.CreateIndex(
                name: "IX_license_documents_FileId",
                table: "license_documents",
                column: "FileId");

            migrationBuilder.CreateIndex(
                name: "IX_license_documents_LicenseId_FileId",
                table: "license_documents",
                columns: new[] { "LicenseId", "FileId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_license_documents_RenewalId",
                table: "license_documents",
                column: "RenewalId");

            migrationBuilder.CreateIndex(
                name: "IX_license_renewals_LicenseId_On",
                table: "license_renewals",
                columns: new[] { "LicenseId", "On" });

            migrationBuilder.CreateIndex(
                name: "IX_license_sensor_links_LicenseId_SensorId",
                table: "license_sensor_links",
                columns: new[] { "LicenseId", "SensorId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_license_sensor_links_SensorId",
                table: "license_sensor_links",
                column: "SensorId");

            migrationBuilder.CreateIndex(
                name: "IX_license_templates_CategoryId",
                table: "license_templates",
                column: "CategoryId");

            migrationBuilder.CreateIndex(
                name: "IX_license_templates_NormalizedName",
                table: "license_templates",
                column: "NormalizedName",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_licenses_CategoryId",
                table: "licenses",
                column: "CategoryId");

            migrationBuilder.CreateIndex(
                name: "IX_licenses_LicenseKeyHint",
                table: "licenses",
                column: "LicenseKeyHint");

            migrationBuilder.CreateIndex(
                name: "IX_licenses_LocationId",
                table: "licenses",
                column: "LocationId");

            migrationBuilder.CreateIndex(
                name: "IX_licenses_Number",
                table: "licenses",
                column: "Number",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_licenses_OwnerId",
                table: "licenses",
                column: "OwnerId");

            migrationBuilder.CreateIndex(
                name: "IX_licenses_ProductKeyHint",
                table: "licenses",
                column: "ProductKeyHint");

            migrationBuilder.CreateIndex(
                name: "IX_licenses_Status",
                table: "licenses",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_licenses_TemplateId",
                table: "licenses",
                column: "TemplateId");

            migrationBuilder.AddForeignKey(
                name: "FK_file_uses_licenses_LicenseId",
                table: "file_uses",
                column: "LicenseId",
                principalTable: "licenses",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            // docs/licences-plan.md §3.1, §3.10: the eleven categories, into their new, empty table; the switch doesn't matter.
            var at = new DateTime(2026, 11, 27, 0, 0, 0, DateTimeKind.Utc);
            migrationBuilder.InsertData(
                table: "license_categories",
                columns: ["Id", "Name", "NormalizedName", "Description", "SortOrder", "DefaultFor", "CreatedAt", "UpdatedAt"],
                values: new object?[,]
                {
                    { new Guid("0f1ce000-0000-4000-8000-000000000001"), "Software", "SOFTWARE", "Licences for programs and operating systems.", 1, "License", at, at },
                    { new Guid("0f1ce000-0000-4000-8000-000000000002"), "Cloud & SaaS", "CLOUD & SAAS", "Cloud platforms and software as a service.", 2, null, at, at },
                    { new Guid("0f1ce000-0000-4000-8000-000000000003"), "Hosting & infrastructure", "HOSTING & INFRASTRUCTURE", "Servers, data centre space, colocation.", 3, null, at, at },
                    { new Guid("0f1ce000-0000-4000-8000-000000000004"), "Connectivity", "CONNECTIVITY", "Internet, WAN, VPN.", 4, null, at, at },
                    { new Guid("0f1ce000-0000-4000-8000-000000000005"), "Telephony & mobile", "TELEPHONY & MOBILE", "Phone lines, mobile contracts, telephony systems.", 5, null, at, at },
                    { new Guid("0f1ce000-0000-4000-8000-000000000006"), "Security", "SECURITY", "Antivirus, firewalls' subscriptions, certificates of trust.", 6, null, at, at },
                    { new Guid("0f1ce000-0000-4000-8000-000000000007"), "Support & maintenance", "SUPPORT & MAINTENANCE", "Maintenance, vendor support, extended warranties.", 7, null, at, at },
                    { new Guid("0f1ce000-0000-4000-8000-000000000008"), "Hardware", "HARDWARE", "Devices bought or leased.", 8, "Hardware", at, at },
                    { new Guid("0f1ce000-0000-4000-8000-000000000009"), "Domains & certificates", "DOMAINS & CERTIFICATES", "Domain names and TLS certificates.", 9, null, at, at },
                    { new Guid("0f1ce000-0000-4000-8000-000000000010"), "Consulting & training", "CONSULTING & TRAINING", "Projects, installations, courses.", 10, null, at, at },
                    { new Guid("0f1ce000-0000-4000-8000-000000000011"), "Other", "OTHER", "Anything else.", 11, "Service", at, at },
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_file_uses_licenses_LicenseId",
                table: "file_uses");

            migrationBuilder.DropTable(
                name: "license_assignments");

            migrationBuilder.DropTable(
                name: "license_documents");

            migrationBuilder.DropTable(
                name: "license_sensor_links");

            migrationBuilder.DropTable(
                name: "license_charges");

            migrationBuilder.DropTable(
                name: "license_renewals");

            migrationBuilder.DropTable(
                name: "licenses");

            migrationBuilder.DropTable(
                name: "license_templates");

            migrationBuilder.DropTable(
                name: "license_categories");

            migrationBuilder.DropIndex(
                name: "IX_file_uses_LicenseId_FileId",
                table: "file_uses");

            migrationBuilder.DropCheckConstraint(
                name: "CK_file_uses_one_owner",
                table: "file_uses");

            migrationBuilder.DropColumn(
                name: "LicenseId",
                table: "file_uses");

            migrationBuilder.AddCheckConstraint(
                name: "CK_file_uses_one_owner",
                table: "file_uses",
                sql: "num_nonnulls(\"PageId\", \"LocationId\", \"ResponsibilityId\", \"BoardId\") = 1");
        }
    }
}
