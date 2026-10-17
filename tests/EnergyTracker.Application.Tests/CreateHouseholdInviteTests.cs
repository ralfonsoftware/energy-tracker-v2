using System.Text.RegularExpressions;
using EnergyTracker.Application.Ports;
using EnergyTracker.Domain;
using NSubstitute;
using Shouldly;

namespace EnergyTracker.Application.Tests;

public class CreateHouseholdInviteTests
{
    private readonly IHouseholdRepository _repository = Substitute.For<IHouseholdRepository>();

    [Fact]
    public async Task Creates_a_single_use_invite_expiring_seven_days_out_and_persists_only_the_hash()
    {
        var householdId = Guid.NewGuid();
        var sut = new CreateHouseholdInvite(_repository);
        HouseholdInvite? stored = null;
        await _repository.AddInviteAsync(Arg.Do<HouseholdInvite>(i => stored = i), Arg.Any<CancellationToken>());

        var before = DateTimeOffset.UtcNow;
        var created = await sut.ExecuteAsync(householdId, TestContext.Current.CancellationToken);
        var after = DateTimeOffset.UtcNow;

        Regex.IsMatch(created.Token, "^[A-Za-z0-9_-]{43}$").ShouldBeTrue();
        stored.ShouldNotBeNull();
        stored.HouseholdId.ShouldBe(householdId);
        stored.Id.ShouldNotBe(Guid.Empty);
        stored.Token.ShouldBeNull();
        stored.TokenHash.ShouldBe(InviteToken.Hash(created.Token));
        stored.ConsumedAtUtc.ShouldBeNull();
        (stored.ExpiresAtUtc - stored.CreatedAtUtc).ShouldBe(CreateHouseholdInvite.InviteLifetime);
        stored.CreatedAtUtc.ShouldBeInRange(before, after);
        created.ExpiresAtUtc.ShouldBe(stored.ExpiresAtUtc);
    }

    [Fact]
    public async Task Each_invite_gets_a_distinct_high_entropy_token()
    {
        var sut = new CreateHouseholdInvite(_repository);

        var first = await sut.ExecuteAsync(Guid.NewGuid(), TestContext.Current.CancellationToken);
        var second = await sut.ExecuteAsync(Guid.NewGuid(), TestContext.Current.CancellationToken);

        first.Token.ShouldNotBe(second.Token);
    }
}
