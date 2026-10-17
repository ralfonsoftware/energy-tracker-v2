using EnergyTracker.Application.Ports;
using EnergyTracker.Domain;
using NSubstitute;
using Shouldly;

namespace EnergyTracker.Application.Tests;

public class ListHouseholdMembersTests
{
    private readonly IHouseholdRepository _repository = Substitute.For<IHouseholdRepository>();

    [Fact]
    public async Task Lists_members_in_join_order_then_id()
    {
        var householdId = Guid.NewGuid();
        var t0 = new DateTimeOffset(2026, 10, 1, 8, 0, 0, TimeSpan.Zero);
        var first = Member(householdId, t0, new Guid("00000000-0000-0000-0000-000000000002"));
        var tiedLow = Member(householdId, t0.AddDays(1), new Guid("00000000-0000-0000-0000-000000000001"));
        var tiedHigh = Member(householdId, t0.AddDays(1), new Guid("00000000-0000-0000-0000-000000000009"));
        _repository.ListMembersAsync(householdId, Arg.Any<CancellationToken>())
            .Returns([tiedHigh, first, tiedLow]);

        var members = await new ListHouseholdMembers(_repository).ExecuteAsync(householdId, TestContext.Current.CancellationToken);

        members.Select(m => m.Id).ShouldBe([first.Id, tiedLow.Id, tiedHigh.Id]);
    }

    private static HouseholdMember Member(Guid householdId, DateTimeOffset createdAtUtc, Guid id) => new()
    {
        Id = id,
        HouseholdId = householdId,
        ExternalIssuer = "https://issuer.example",
        ExternalSubjectId = $"sub-{id}",
        CreatedAtUtc = createdAtUtc,
    };
}
