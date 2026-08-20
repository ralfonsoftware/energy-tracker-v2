using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EnergyTracker.Infrastructure.Migrations.SqlServer.Migrations
{
    /// <inheritdoc />
    public partial class AddSmartPlugImportGaps : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SmartPlugImportGaps",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    HouseholdId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SmartPlugImportId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PowerPointId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    StartDate = table.Column<DateOnly>(type: "date", nullable: false),
                    EndDate = table.Column<DateOnly>(type: "date", nullable: false),
                    Treatment = table.Column<int>(type: "int", nullable: false),
                    EstimatedTotalKwh = table.Column<decimal>(type: "decimal(18,6)", precision: 18, scale: 6, nullable: true),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SmartPlugImportGaps", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SmartPlugImportGaps_Households_HouseholdId",
                        column: x => x.HouseholdId,
                        principalTable: "Households",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SmartPlugImportGaps_PowerPoints_PowerPointId",
                        column: x => x.PowerPointId,
                        principalTable: "PowerPoints",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SmartPlugImportGaps_SmartPlugImports_SmartPlugImportId",
                        column: x => x.SmartPlugImportId,
                        principalTable: "SmartPlugImports",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SmartPlugImportGaps_HouseholdId",
                table: "SmartPlugImportGaps",
                column: "HouseholdId");

            migrationBuilder.CreateIndex(
                name: "IX_SmartPlugImportGaps_PowerPointId",
                table: "SmartPlugImportGaps",
                column: "PowerPointId");

            migrationBuilder.CreateIndex(
                name: "IX_SmartPlugImportGaps_SmartPlugImportId",
                table: "SmartPlugImportGaps",
                column: "SmartPlugImportId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SmartPlugImportGaps");
        }
    }
}
