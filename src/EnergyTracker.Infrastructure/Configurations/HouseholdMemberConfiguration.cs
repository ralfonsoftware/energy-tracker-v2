using EnergyTracker.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EnergyTracker.Infrastructure.Configurations;

public class HouseholdMemberConfiguration : IEntityTypeConfiguration<HouseholdMember>
{
    public void Configure(EntityTypeBuilder<HouseholdMember> builder)
    {
        builder.ToTable("HouseholdMembers");

        builder.HasKey(m => m.Id);

        // 500 chars (well above any realistic OIDC issuer URL) keeps the composite unique index
        // below both providers' index key-size limits when combined with ExternalSubjectId's
        // 256 — SQL Server's ~1700-byte nonclustered key limit is the tighter constraint
        // (500 + 256 chars * 2 bytes/nvarchar-char = 1512 bytes); the previous 2048 could exceed
        // it and fail at INSERT time (a provider-inconsistent failure mode AD-2 is meant to avoid).
        builder.Property(m => m.ExternalIssuer)
            .HasMaxLength(500)
            .IsRequired();

        builder.Property(m => m.ExternalSubjectId)
            .HasMaxLength(256)
            .IsRequired();

        builder.Property(m => m.CreatedAtUtc)
            .IsRequired();

        // Every real query against HouseholdMember is either the identity-resolution lookup by
        // ExternalIssuer+ExternalSubjectId (globally scoped by design, not Household-scoped) or
        // one already anchored to a known-trusted HouseholdId. Applying the standard AD-3
        // HasQueryFilter here would create a circular dependency: ICurrentHouseholdAccessor must
        // look up this row *to determine* HouseholdId before that value exists to filter by, and
        // AD-3 explicitly forbids IgnoreQueryFilters() as the workaround. This is a deliberate,
        // reasoned exception to AD-3's general rule — see story 1.5 Completion Notes.
        builder.HasIndex(m => new { m.ExternalIssuer, m.ExternalSubjectId })
            .IsUnique();
    }
}
