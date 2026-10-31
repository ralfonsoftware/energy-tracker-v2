using EnergyTracker.Application.Ports;
using NSubstitute;
using Shouldly;

namespace EnergyTracker.Application.Tests;

public class RevokeHouseholdInviteTests
{
    private readonly IHouseholdRepository _repository = Substitute.For<IHouseholdRepository>();

    [Fact]
    public async Task Revokes_through_the_repository_scoped_to_the_callers_Household()
    {
        var householdId = Guid.NewGuid();
        var inviteId = Guid.NewGuid();
        var before = DateTimeOffset.UtcNow;

        await new RevokeHouseholdInvite(_repository, TimeProvider.System).ExecuteAsync(householdId, inviteId, TestContext.Current.CancellationToken);

        await _repository.Received(1).RevokeInviteAsync(
            householdId,
            inviteId,
            Arg.Is<DateTimeOffset>(now => now >= before && now <= DateTimeOffset.UtcNow),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Surfaces_an_invite_that_is_not_open_in_the_Household()
    {
        var householdId = Guid.NewGuid();
        var inviteId = Guid.NewGuid();
        _repository.RevokeInviteAsync(householdId, inviteId, Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new HouseholdInviteNotOpenException()));

        await Should.ThrowAsync<HouseholdInviteNotOpenException>(() =>
            new RevokeHouseholdInvite(_repository, TimeProvider.System).ExecuteAsync(householdId, inviteId, TestContext.Current.CancellationToken));
    }
}
