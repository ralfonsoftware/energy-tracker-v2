using EnergyTracker.Application.Ports;
using EnergyTracker.Domain;
using NSubstitute;
using Shouldly;

namespace EnergyTracker.Application.Tests;

public class AcceptHouseholdInviteTests
{
    private readonly IHouseholdRepository _repository = Substitute.For<IHouseholdRepository>();

    private static HouseholdInvite MakeInvite(Guid? householdId = null, DateTimeOffset? expiresAtUtc = null, DateTimeOffset? consumedAtUtc = null) => new()
    {
        Id = Guid.NewGuid(),
        HouseholdId = householdId ?? Guid.NewGuid(),
        Token = Guid.NewGuid().ToString("N"),
        CreatedAtUtc = DateTimeOffset.UtcNow.AddDays(-1),
        ExpiresAtUtc = expiresAtUtc ?? DateTimeOffset.UtcNow.AddDays(6),
        ConsumedAtUtc = consumedAtUtc,
    };

    [Fact]
    public async Task Joins_the_invites_Household_and_marks_the_invite_consumed_for_a_new_principal()
    {
        var invite = MakeInvite();
        var joinedHousehold = new Household { Id = invite.HouseholdId, Locale = "de-DE", Currency = "EUR", CreatedAtUtc = DateTimeOffset.UtcNow };
        _repository.FindInviteByTokenAsync(invite.Token, Arg.Any<CancellationToken>()).Returns(invite);
        _repository.FindMemberAsync("https://issuer.example", "subject-1", Arg.Any<CancellationToken>()).Returns((HouseholdMember?)null);
        _repository.AcceptInviteAsync(invite, Arg.Any<HouseholdMember>(), Arg.Any<CancellationToken>()).Returns(joinedHousehold);
        var sut = new AcceptHouseholdInvite(_repository);

        var household = await sut.ExecuteAsync(invite.Token, "https://issuer.example", "subject-1", "Mira", TestContext.Current.CancellationToken);

        household.ShouldBe(joinedHousehold);
        await _repository.Received(1).AcceptInviteAsync(
            invite,
            Arg.Is<HouseholdMember>(m =>
                m.HouseholdId == invite.HouseholdId &&
                m.ExternalIssuer == "https://issuer.example" &&
                m.ExternalSubjectId == "subject-1" &&
                m.DisplayName == "Mira"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Rejects_an_unknown_token()
    {
        _repository.FindInviteByTokenAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns((HouseholdInvite?)null);
        var sut = new AcceptHouseholdInvite(_repository);

        await Should.ThrowAsync<HouseholdInviteNotFoundException>(() =>
            sut.ExecuteAsync("unknown-token", "https://issuer.example", "subject-1", "Mira", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Rejects_an_expired_invite()
    {
        var invite = MakeInvite(expiresAtUtc: DateTimeOffset.UtcNow.AddMinutes(-1));
        _repository.FindInviteByTokenAsync(invite.Token, Arg.Any<CancellationToken>()).Returns(invite);
        var sut = new AcceptHouseholdInvite(_repository);

        await Should.ThrowAsync<HouseholdInviteExpiredOrConsumedException>(() =>
            sut.ExecuteAsync(invite.Token, "https://issuer.example", "subject-1", "Mira", TestContext.Current.CancellationToken));

        await _repository.DidNotReceive().AcceptInviteAsync(Arg.Any<HouseholdInvite>(), Arg.Any<HouseholdMember>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Rejects_an_already_consumed_invite()
    {
        var invite = MakeInvite(consumedAtUtc: DateTimeOffset.UtcNow.AddMinutes(-1));
        _repository.FindInviteByTokenAsync(invite.Token, Arg.Any<CancellationToken>()).Returns(invite);
        var sut = new AcceptHouseholdInvite(_repository);

        await Should.ThrowAsync<HouseholdInviteExpiredOrConsumedException>(() =>
            sut.ExecuteAsync(invite.Token, "https://issuer.example", "subject-1", "Mira", TestContext.Current.CancellationToken));

        await _repository.DidNotReceive().AcceptInviteAsync(Arg.Any<HouseholdInvite>(), Arg.Any<HouseholdMember>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Rejects_a_principal_that_already_belongs_to_a_Household()
    {
        var invite = MakeInvite();
        var existingHouseholdId = Guid.NewGuid();
        _repository.FindInviteByTokenAsync(invite.Token, Arg.Any<CancellationToken>()).Returns(invite);
        _repository.FindMemberAsync("https://issuer.example", "subject-1", Arg.Any<CancellationToken>()).Returns(new HouseholdMember
        {
            Id = Guid.NewGuid(),
            HouseholdId = existingHouseholdId,
            ExternalIssuer = "https://issuer.example",
            ExternalSubjectId = "subject-1",
            CreatedAtUtc = DateTimeOffset.UtcNow,
        });
        var sut = new AcceptHouseholdInvite(_repository);

        var exception = await Should.ThrowAsync<HouseholdAlreadyExistsException>(() =>
            sut.ExecuteAsync(invite.Token, "https://issuer.example", "subject-1", "Mira", TestContext.Current.CancellationToken));

        exception.ExistingHouseholdId.ShouldBe(existingHouseholdId);
        await _repository.DidNotReceive().AcceptInviteAsync(Arg.Any<HouseholdInvite>(), Arg.Any<HouseholdMember>(), Arg.Any<CancellationToken>());
    }
}
