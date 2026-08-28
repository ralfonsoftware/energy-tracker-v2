using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EnergyTracker.Infrastructure.Migrations.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class SmartPlugImportJobHistory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_SmartPlugReadings_SmartPlugImports_SmartPlugImportId",
                table: "SmartPlugReadings");

            migrationBuilder.AlterColumn<Guid>(
                name: "SmartPlugImportId",
                table: "SmartPlugReadings",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.AddColumn<string>(
                name: "DisplayName",
                table: "HouseholdMembers",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "OriginalFileName",
                table: "BackgroundJobs",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "QueuedByHouseholdMemberId",
                table: "BackgroundJobs",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_BackgroundJobs_QueuedByHouseholdMemberId",
                table: "BackgroundJobs",
                column: "QueuedByHouseholdMemberId");

            migrationBuilder.AddForeignKey(
                name: "FK_BackgroundJobs_HouseholdMembers_QueuedByHouseholdMemberId",
                table: "BackgroundJobs",
                column: "QueuedByHouseholdMemberId",
                principalTable: "HouseholdMembers",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_SmartPlugReadings_SmartPlugImports_SmartPlugImportId",
                table: "SmartPlugReadings",
                column: "SmartPlugImportId",
                principalTable: "SmartPlugImports",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_BackgroundJobs_HouseholdMembers_QueuedByHouseholdMemberId",
                table: "BackgroundJobs");

            migrationBuilder.DropForeignKey(
                name: "FK_SmartPlugReadings_SmartPlugImports_SmartPlugImportId",
                table: "SmartPlugReadings");

            migrationBuilder.DropIndex(
                name: "IX_BackgroundJobs_QueuedByHouseholdMemberId",
                table: "BackgroundJobs");

            migrationBuilder.DropColumn(
                name: "DisplayName",
                table: "HouseholdMembers");

            migrationBuilder.DropColumn(
                name: "OriginalFileName",
                table: "BackgroundJobs");

            migrationBuilder.DropColumn(
                name: "QueuedByHouseholdMemberId",
                table: "BackgroundJobs");

            migrationBuilder.AlterColumn<Guid>(
                name: "SmartPlugImportId",
                table: "SmartPlugReadings",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);

            migrationBuilder.AddForeignKey(
                name: "FK_SmartPlugReadings_SmartPlugImports_SmartPlugImportId",
                table: "SmartPlugReadings",
                column: "SmartPlugImportId",
                principalTable: "SmartPlugImports",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }
    }
}
