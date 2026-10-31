using EnergyTracker.Application.Ports;
using EnergyTracker.Domain;
using NSubstitute;
using Shouldly;

namespace EnergyTracker.Application.Tests;

public class ListOpenHouseholdInvitesTests
{
    private readonly IHouseholdRepository _repository = Substitute.For<IHouseholdRepository>();

    [Fact]
    public async Task Lists_open_invites_newest_first_using_the_current_time()
    {
        var householdId = Guid.NewGuid();
        var older = Invite(householdId, DateTimeOffset.UtcNow.AddDays(-2));
        var newer = Invite(householdId, DateTimeOffset.UtcNow.AddDays(-1));
        var before = DateTimeOffset.UtcNow;
        _repository.ListOpenInvitesAsync(householdId, Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns([older, newer]);

        var invites = await new ListOpenHouseholdInvites(_repository, TimeProvider.System).ExecuteAsync(householdId, TestContext.Current.CancellationToken);

        invites.Select(i => i.Id).ShouldBe([newer.Id, older.Id]);
        await _repository.Received(1).ListOpenInvitesAsync(
            householdId,
            Arg.Is<DateTimeOffset>(now => now >= before && now <= DateTimeOffset.UtcNow),
            Arg.Any<CancellationToken>());
    }

    private static HouseholdInvite Invite(Guid householdId, DateTimeOffset createdAtUtc) => new()
    {
        Id = Guid.NewGuid(),
        HouseholdId = householdId,
        TokenHash = new string('a', 64),
        CreatedAtUtc = createdAtUtc,
        ExpiresAtUtc = createdAtUtc.AddDays(7),
    };
}
