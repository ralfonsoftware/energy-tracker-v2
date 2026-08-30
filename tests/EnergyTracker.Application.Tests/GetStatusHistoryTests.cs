using EnergyTracker.Application.Ports;
using EnergyTracker.Domain;
using NSubstitute;
using Shouldly;

namespace EnergyTracker.Application.Tests;

public class GetStatusHistoryTests
{
    private readonly IHouseholdRepository _householdRepository = Substitute.For<IHouseholdRepository>();
    private readonly IStatusSnapshotRepository _statusSnapshotRepository = Substitute.For<IStatusSnapshotRepository>();

    private GetStatusHistory Sut() => new(_householdRepository, _statusSnapshotRepository);

    private static Household NewHousehold(Guid id, int lowConfidenceGapDays = 45) => new()
    {
        Id = id,
        Locale = "en-US",
        Currency = "USD",
        CreatedAtUtc = DateTimeOffset.UtcNow,
        LowConfidenceGapDays = lowConfidenceGapDays,
    };

    private static StatusSnapshot NewSnapshot(Guid householdId, DateTimeOffset computedAtUtc, Status status = Status.WithinRange) => new()
    {
        Id = Guid.NewGuid(),
        HouseholdId = householdId,
        Status = status,
        PaceToDateKwh = 100m,
        BaselineToDateKwh = 100m,
        IsLowConfidence = false,
        ComputedAtUtc = computedAtUtc,
    };

    [Fact]
    public async Task Returns_an_empty_list_when_the_Household_does_not_exist()
    {
        var householdId = Guid.NewGuid();
        _householdRepository.FindByIdAsync(householdId, Arg.Any<CancellationToken>()).Returns((Household?)null);
        var sut = Sut();

        var result = await sut.ExecuteAsync(householdId, TestContext.Current.CancellationToken);

        result.ShouldBeEmpty();
        await _statusSnapshotRepository.DidNotReceive().GetForHouseholdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Returns_an_empty_list_when_no_StatusSnapshot_rows_exist()
    {
        var householdId = Guid.NewGuid();
        _householdRepository.FindByIdAsync(householdId, Arg.Any<CancellationToken>()).Returns(NewHousehold(householdId));
        _statusSnapshotRepository.GetForHouseholdAsync(householdId, Arg.Any<CancellationToken>()).Returns([]);
        var sut = Sut();

        var result = await sut.ExecuteAsync(householdId, TestContext.Current.CancellationToken);

        result.ShouldBeEmpty();
    }

    [Fact]
    public async Task Maps_snapshots_to_entries_preserving_the_repositorys_ascending_order()
    {
        var householdId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var first = NewSnapshot(householdId, now.AddDays(-10), Status.WithinRange);
        var second = NewSnapshot(householdId, now, Status.Trending);
        _householdRepository.FindByIdAsync(householdId, Arg.Any<CancellationToken>()).Returns(NewHousehold(householdId));
        _statusSnapshotRepository.GetForHouseholdAsync(householdId, Arg.Any<CancellationToken>()).Returns([first, second]);
        var sut = Sut();

        var result = await sut.ExecuteAsync(householdId, TestContext.Current.CancellationToken);

        result.Count.ShouldBe(2);
        result[0].ComputedAtUtc.ShouldBe(first.ComputedAtUtc);
        result[0].Status.ShouldBe(Status.WithinRange);
        result[1].ComputedAtUtc.ShouldBe(second.ComputedAtUtc);
        result[1].Status.ShouldBe(Status.Trending);
    }

    [Fact]
    public async Task The_first_entrys_GapBeforeThisEntry_is_always_false()
    {
        var householdId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var only = NewSnapshot(householdId, now.AddDays(-1000));
        _householdRepository.FindByIdAsync(householdId, Arg.Any<CancellationToken>()).Returns(NewHousehold(householdId, lowConfidenceGapDays: 1));
        _statusSnapshotRepository.GetForHouseholdAsync(householdId, Arg.Any<CancellationToken>()).Returns([only]);
        var sut = Sut();

        var result = await sut.ExecuteAsync(householdId, TestContext.Current.CancellationToken);

        result[0].GapBeforeThisEntry.ShouldBeFalse();
    }

    [Fact]
    public async Task GapBeforeThisEntry_is_false_for_a_pair_within_LowConfidenceGapDays()
    {
        var householdId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var first = NewSnapshot(householdId, now.AddDays(-10));
        var second = NewSnapshot(householdId, now);
        _householdRepository.FindByIdAsync(householdId, Arg.Any<CancellationToken>()).Returns(NewHousehold(householdId, lowConfidenceGapDays: 45));
        _statusSnapshotRepository.GetForHouseholdAsync(householdId, Arg.Any<CancellationToken>()).Returns([first, second]);
        var sut = Sut();

        var result = await sut.ExecuteAsync(householdId, TestContext.Current.CancellationToken);

        result[1].GapBeforeThisEntry.ShouldBeFalse();
    }

    [Fact]
    public async Task GapBeforeThisEntry_is_true_for_a_pair_exceeding_LowConfidenceGapDays()
    {
        var householdId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var first = NewSnapshot(householdId, now.AddDays(-50));
        var second = NewSnapshot(householdId, now);
        _householdRepository.FindByIdAsync(householdId, Arg.Any<CancellationToken>()).Returns(NewHousehold(householdId, lowConfidenceGapDays: 45));
        _statusSnapshotRepository.GetForHouseholdAsync(householdId, Arg.Any<CancellationToken>()).Returns([first, second]);
        var sut = Sut();

        var result = await sut.ExecuteAsync(householdId, TestContext.Current.CancellationToken);

        result[1].GapBeforeThisEntry.ShouldBeTrue();
    }

    [Fact]
    public async Task Respects_a_non_default_LowConfidenceGapDays_value_instead_of_a_hardcoded_45()
    {
        var householdId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var first = NewSnapshot(householdId, now.AddDays(-10));
        var second = NewSnapshot(householdId, now);
        // A gap of 10 days exceeds a LowConfidenceGapDays of 5 — would be false under the 45-day
        // default, so this only passes if the use case reads the Household's own configured value.
        _householdRepository.FindByIdAsync(householdId, Arg.Any<CancellationToken>()).Returns(NewHousehold(householdId, lowConfidenceGapDays: 5));
        _statusSnapshotRepository.GetForHouseholdAsync(householdId, Arg.Any<CancellationToken>()).Returns([first, second]);
        var sut = Sut();

        var result = await sut.ExecuteAsync(householdId, TestContext.Current.CancellationToken);

        result[1].GapBeforeThisEntry.ShouldBeTrue();
    }
}
