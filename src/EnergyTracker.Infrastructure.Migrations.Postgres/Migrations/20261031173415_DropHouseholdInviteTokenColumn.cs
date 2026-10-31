using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EnergyTracker.Infrastructure.Migrations.Postgres.Migrations
{
    // Story 11.20 (AD-26): contract step of the invite-token hash change, shipped in the same release
    // as the code that stops reading Token. This is a justified exception to the expand/contract rule.
    // Safeguards: the production gate query returned 0 rows (no live legacy invite); the column holds
    // only expired plaintext tokens, so nothing of value is lost; the Story 10.1 restore point is taken
    // before this migration. Rollback is that restore point, not Down(), which restores no values.
    /// <inheritdoc />
    public partial class DropHouseholdInviteTokenColumn : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_HouseholdInvites_Token",
                table: "HouseholdInvites");

            migrationBuilder.DropColumn(
                name: "Token",
                table: "HouseholdInvites");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Token",
                table: "HouseholdInvites",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_HouseholdInvites_Token",
                table: "HouseholdInvites",
                column: "Token",
                unique: true);
        }
    }
}
