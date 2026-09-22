using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EnergyTracker.Infrastructure.Migrations.SqlServer.Migrations
{
    /// <inheritdoc />
    public partial class BackgroundJobQueuedByMemberSetNull : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_BackgroundJobs_HouseholdMembers_QueuedByHouseholdMemberId",
                table: "BackgroundJobs");

            migrationBuilder.AddForeignKey(
                name: "FK_BackgroundJobs_HouseholdMembers_QueuedByHouseholdMemberId",
                table: "BackgroundJobs",
                column: "QueuedByHouseholdMemberId",
                principalTable: "HouseholdMembers",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_BackgroundJobs_HouseholdMembers_QueuedByHouseholdMemberId",
                table: "BackgroundJobs");

            migrationBuilder.AddForeignKey(
                name: "FK_BackgroundJobs_HouseholdMembers_QueuedByHouseholdMemberId",
                table: "BackgroundJobs",
                column: "QueuedByHouseholdMemberId",
                principalTable: "HouseholdMembers",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }
    }
}
