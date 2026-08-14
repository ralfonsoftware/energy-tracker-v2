using EnergyTracker.Application.Ports;
using EnergyTracker.Domain;
using NSubstitute;
using Shouldly;

namespace EnergyTracker.Application.Tests;

public class CreateHouseholdInviteTests
{
    private readonly IHouseholdRepository _repository = Substitute.For<IHouseholdRepository>();

    [Fact]
    public async Task Creates_a_single_use_invite_expiring_seven_days_out_and_persists_it()
    {
        var householdId = Guid.NewGuid();
        var sut = new CreateHouseholdInvite(_repository);

        var before = DateTimeOffset.UtcNow;
        var invite = await sut.ExecuteAsync(householdId, TestContext.Current.CancellationToken);
        var after = DateTimeOffset.UtcNow;

        invite.HouseholdId.ShouldBe(householdId);
        invite.Id.ShouldNotBe(Guid.Empty);
        invite.Token.ShouldNotBeNullOrWhiteSpace();
        invite.Token.Length.ShouldBe(32);
        invite.ConsumedAtUtc.ShouldBeNull();
        (invite.ExpiresAtUtc - invite.CreatedAtUtc).ShouldBe(CreateHouseholdInvite.InviteLifetime);
        invite.CreatedAtUtc.ShouldBeInRange(before, after);

        await _repository.Received(1).AddInviteAsync(
            Arg.Is<HouseholdInvite>(i => i.Id == invite.Id && i.HouseholdId == householdId && i.Token == invite.Token),
            Arg.Any<CancellationToken>());
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
