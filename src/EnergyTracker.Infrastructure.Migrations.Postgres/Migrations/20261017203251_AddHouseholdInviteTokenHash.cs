using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EnergyTracker.Infrastructure.Migrations.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class AddHouseholdInviteTokenHash : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Story 11.9 (AD-26, audit S10): expand-only. TokenHash is a new nullable column and Token
            // becomes nullable; nothing is dropped, renamed or narrowed. The previous image stays safe: it
            // only reads HouseholdInvites with WHERE Token = @t (a NULL-Token row never matches) and it
            // inserts rows with a Token, which the new lookup finds through its legacy path. Invites
            // created by this release have no Token, so a rolled-back image answers 404 for them.
            migrationBuilder.AlterColumn<string>(
                name: "Token",
                table: "HouseholdInvites",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(32)",
                oldMaxLength: 32);

            migrationBuilder.AddColumn<string>(
                name: "TokenHash",
                table: "HouseholdInvites",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_HouseholdInvites_TokenHash",
                table: "HouseholdInvites",
                column: "TokenHash");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_HouseholdInvites_TokenHash",
                table: "HouseholdInvites");

            migrationBuilder.DropColumn(
                name: "TokenHash",
                table: "HouseholdInvites");

            migrationBuilder.AlterColumn<string>(
                name: "Token",
                table: "HouseholdInvites",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "character varying(32)",
                oldMaxLength: 32,
                oldNullable: true);
        }
    }
}
