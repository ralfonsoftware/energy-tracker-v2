using EnergyTracker.Application.Ports;
using EnergyTracker.Domain;
using NSubstitute;
using Shouldly;

namespace EnergyTracker.Application.Tests;

public class SetYearlyBaselineTests
{
    private readonly IHouseholdRepository _repository = Substitute.For<IHouseholdRepository>();

    [Fact]
    public async Task Updates_the_Yearly_Baseline_via_the_repository_with_the_expected_version()
    {
        var householdId = Guid.NewGuid();
        var updated = new Household
        {
            Id = householdId,
            Locale = "de-DE",
            Currency = "EUR",
            CreatedAtUtc = DateTimeOffset.UtcNow,
            YearlyBaselineKwh = 3500m,
            Version = 2,
        };
        _repository.UpdateYearlyBaselineAsync(householdId, 3500m, 1, Arg.Any<CancellationToken>())
            .Returns(updated);
        var sut = new SetYearlyBaseline(_repository);

        var result = await sut.ExecuteAsync(householdId, 3500m, 1, TestContext.Current.CancellationToken);

        result.ShouldBe(updated);
        await _repository.Received(1).UpdateYearlyBaselineAsync(householdId, 3500m, 1, Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-3500.5)]
    public async Task Rejects_a_Yearly_Baseline_that_is_not_a_positive_value(decimal yearlyBaselineKwh)
    {
        var sut = new SetYearlyBaseline(_repository);

        await Should.ThrowAsync<HouseholdValidationException>(() =>
            sut.ExecuteAsync(Guid.NewGuid(), yearlyBaselineKwh, 1, TestContext.Current.CancellationToken));

        await _repository.DidNotReceive().UpdateYearlyBaselineAsync(
            Arg.Any<Guid>(), Arg.Any<decimal>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_concurrency_conflict_thrown_by_the_repository_propagates_unchanged()
    {
        var householdId = Guid.NewGuid();
        _repository.UpdateYearlyBaselineAsync(householdId, 3500m, 1, Arg.Any<CancellationToken>())
            .Returns<Task<Household>>(_ => throw new HouseholdConcurrencyConflictException(householdId));
        var sut = new SetYearlyBaseline(_repository);

        await Should.ThrowAsync<HouseholdConcurrencyConflictException>(() =>
            sut.ExecuteAsync(householdId, 3500m, 1, TestContext.Current.CancellationToken));
    }
}
