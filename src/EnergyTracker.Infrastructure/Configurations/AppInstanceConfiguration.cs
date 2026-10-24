using EnergyTracker.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EnergyTracker.Infrastructure.Configurations;

public class AppInstanceConfiguration : IEntityTypeConfiguration<AppInstance>
{
    public void Configure(EntityTypeBuilder<AppInstance> builder)
    {
        builder.ToTable("AppInstances");

        // The id is generated once per process start, never by the database.
        builder.HasKey(a => a.Id);
        builder.Property(a => a.Id).ValueGeneratedNever();

        builder.Property(a => a.StartedAtUtc).IsRequired();
        builder.Property(a => a.LastSeenAtUtc).IsRequired();

        // The alive and purge queries both filter on LastSeenAtUtc.
        builder.HasIndex(a => a.LastSeenAtUtc);

        // AD-24 / AD-3 exemption: instance-level operational data, no HouseholdId, so no query filter
        // is wired in EnergyTrackerDbContext.OnModelCreating.
    }
}
