using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EnergyTracker.Infrastructure.Migrations.SqlServer.Migrations
{
    /// <inheritdoc />
    public partial class AddMainMeterAndMeterReading : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "MainMeters",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    HouseholdId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MainMeters", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MainMeters_Households_HouseholdId",
                        column: x => x.HouseholdId,
                        principalTable: "Households",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "MeterReadings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    HouseholdId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    MainMeterId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    KwhValue = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    ReadingTimestamp = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    IdempotencyKey = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MeterReadings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MeterReadings_Households_HouseholdId",
                        column: x => x.HouseholdId,
                        principalTable: "Households",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_MeterReadings_MainMeters_MainMeterId",
                        column: x => x.MainMeterId,
                        principalTable: "MainMeters",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MainMeters_HouseholdId",
                table: "MainMeters",
                column: "HouseholdId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MeterReadings_HouseholdId",
                table: "MeterReadings",
                column: "HouseholdId");

            migrationBuilder.CreateIndex(
                name: "IX_MeterReadings_IdempotencyKey",
                table: "MeterReadings",
                column: "IdempotencyKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MeterReadings_MainMeterId",
                table: "MeterReadings",
                column: "MainMeterId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MeterReadings");

            migrationBuilder.DropTable(
                name: "MainMeters");
        }
    }
}
