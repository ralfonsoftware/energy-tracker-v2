using EnergyTracker.Application.Ports;
using EnergyTracker.Domain;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace EnergyTracker.Infrastructure.Tests;

public class HouseholdInviteModelTests
{
    [Fact]
    public void HouseholdInvite_maps_only_the_token_hash_and_no_plaintext_token_column()
    {
        var options = new DbContextOptionsBuilder<EnergyTrackerDbContext>().UseNpgsql("Host=unused").Options;
        using var dbContext = new EnergyTrackerDbContext(options, new NoOpAccessor());

        var entity = dbContext.Model.FindEntityType(typeof(HouseholdInvite)).ShouldNotBeNull();

        // Non-vacuous: the hash property and its index are found, so a broken lookup cannot pass.
        var tokenHash = entity.FindProperty(nameof(HouseholdInvite.TokenHash)).ShouldNotBeNull();
        entity.GetIndexes().ShouldContain(i => i.Properties.Contains(tokenHash));
        entity.FindProperty("Token").ShouldBeNull();
        entity.GetIndexes().ShouldNotContain(i => i.Properties.Any(p => p.Name == "Token"));
    }

    private sealed class NoOpAccessor : ICurrentHouseholdAccessor
    {
        public Guid? HouseholdId => null;

        public Guid? HouseholdMemberId => null;
    }
}
