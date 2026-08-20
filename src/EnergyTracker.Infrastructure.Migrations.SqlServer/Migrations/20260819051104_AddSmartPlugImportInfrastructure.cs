using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EnergyTracker.Infrastructure.Migrations.SqlServer.Migrations
{
    /// <inheritdoc />
    public partial class AddSmartPlugImportInfrastructure : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "BackgroundJobs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    HouseholdId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    JobType = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    ErrorMessage = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CompletedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BackgroundJobs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_BackgroundJobs_Households_HouseholdId",
                        column: x => x.HouseholdId,
                        principalTable: "Households",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "SmartPlugImports",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    HouseholdId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BackgroundJobId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    VendorFormat = table.Column<int>(type: "int", nullable: false),
                    OriginalFileName = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    DeviceTag = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CompletedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SmartPlugImports", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SmartPlugImports_BackgroundJobs_BackgroundJobId",
                        column: x => x.BackgroundJobId,
                        principalTable: "BackgroundJobs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SmartPlugImports_Households_HouseholdId",
                        column: x => x.HouseholdId,
                        principalTable: "Households",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "SmartPlugReadings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    HouseholdId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SmartPlugImportId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PowerPointId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RoomName = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    PowerPointName = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    DeviceName = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    IntervalStart = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    IntervalEnd = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    KwhValue = table.Column<decimal>(type: "decimal(18,6)", precision: 18, scale: 6, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SmartPlugReadings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SmartPlugReadings_Households_HouseholdId",
                        column: x => x.HouseholdId,
                        principalTable: "Households",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SmartPlugReadings_PowerPoints_PowerPointId",
                        column: x => x.PowerPointId,
                        principalTable: "PowerPoints",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SmartPlugReadings_SmartPlugImports_SmartPlugImportId",
                        column: x => x.SmartPlugImportId,
                        principalTable: "SmartPlugImports",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_BackgroundJobs_HouseholdId",
                table: "BackgroundJobs",
                column: "HouseholdId");

            migrationBuilder.CreateIndex(
                name: "IX_SmartPlugImports_BackgroundJobId",
                table: "SmartPlugImports",
                column: "BackgroundJobId");

            migrationBuilder.CreateIndex(
                name: "IX_SmartPlugImports_HouseholdId",
                table: "SmartPlugImports",
                column: "HouseholdId");

            migrationBuilder.CreateIndex(
                name: "IX_SmartPlugReadings_HouseholdId",
                table: "SmartPlugReadings",
                column: "HouseholdId");

            migrationBuilder.CreateIndex(
                name: "IX_SmartPlugReadings_PowerPointId",
                table: "SmartPlugReadings",
                column: "PowerPointId");

            migrationBuilder.CreateIndex(
                name: "IX_SmartPlugReadings_SmartPlugImportId",
                table: "SmartPlugReadings",
                column: "SmartPlugImportId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SmartPlugReadings");

            migrationBuilder.DropTable(
                name: "SmartPlugImports");

            migrationBuilder.DropTable(
                name: "BackgroundJobs");
        }
    }
}
