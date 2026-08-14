using EnergyTracker.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EnergyTracker.Infrastructure.Configurations;

public class HouseholdInviteConfiguration : IEntityTypeConfiguration<HouseholdInvite>
{
    public void Configure(EntityTypeBuilder<HouseholdInvite> builder)
    {
        builder.ToTable("HouseholdInvites");

        builder.HasKey(i => i.Id);

        builder.Property(i => i.Token)
            .HasMaxLength(32)
            .IsRequired();

        builder.HasIndex(i => i.Token)
            .IsUnique();

        builder.Property(i => i.CreatedAtUtc)
            .IsRequired();

        builder.Property(i => i.ExpiresAtUtc)
            .IsRequired();

        builder.Property(i => i.Version)
            .IsConcurrencyToken();

        // No AD-3 HasQueryFilter here — the exact same reasoned exception
        // HouseholdMemberConfiguration.cs documents for HouseholdMember, for the identical
        // reason. The accept-by-token lookup (FindInviteByTokenAsync) is performed by a
        // principal who, by definition, does not have a resolved HouseholdId yet — that's the
        // entire premise of accepting an invite. If the standard
        // HasQueryFilter(i => i.HouseholdId == _currentHousehold.Id) were applied, comparing
        // HouseholdId (non-nullable Guid) against a null current-household id would filter out
        // every row, and the join flow could never find any invite. HouseholdInvite creation
        // (AddInviteAsync) is always called with an already-known-trusted HouseholdId from the
        // authenticated creator, so the absence of a filter costs nothing on that path either.
        builder.HasOne<Household>()
            .WithMany()
            .HasForeignKey(i => i.HouseholdId)
            .IsRequired()
            .OnDelete(DeleteBehavior.Cascade);
    }
}
