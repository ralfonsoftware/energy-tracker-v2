using EnergyTracker.Application.Ports;
using EnergyTracker.Domain;
using NSubstitute;
using Shouldly;

namespace EnergyTracker.Application.Tests;

public class CompareTariffTests
{
    private readonly GetCurrentStatus _getCurrentStatus = Substitute.For<GetCurrentStatus>(
        Substitute.For<IHouseholdRepository>(),
        Substitute.For<IMeterReadingRepository>(),
        Substitute.For<IMeterRegressionPromptRepository>(),
        Substitute.For<ISmartPlugCoverageSignal>());

    private readonly ITariffRepository _tariffRepository = Substitute.For<ITariffRepository>();

    private CompareTariff Sut() => new(_getCurrentStatus, _tariffRepository);

    private static Tariff CurrentTariff(Guid householdId, decimal monthlyBaseFee, decimal pricePerKwh, string currency) => new()
    {
        Id = Guid.NewGuid(),
        HouseholdId = householdId,
        MonthlyBaseFee = monthlyBaseFee,
        PricePerKwh = pricePerKwh,
        Currency = currency,
        ContractStartDate = DateTimeOffset.UtcNow.AddYears(-1),
        ContractPeriodMonths = 12,
        CreatedAtUtc = DateTimeOffset.UtcNow.AddYears(-1),
        Version = 0,
    };

    private static CurrentStatusResult StatusAtElapsedDays(decimal paceToDateKwh, double elapsedDays) => new(
        Status: Status.WithinRange,
        PaceToDateKwh: paceToDateKwh,
        BaselineToDateKwh: paceToDateKwh,
        IsLowConfidence: false,
        ElapsedDays: elapsedDays,
        TrendingThresholdKwh: 500m,
        DaysSinceLastReading: 0,
        LowConfidenceGapDaysThreshold: 60);

    [Fact]
    public async Task A_valid_comparison_at_exactly_one_year_elapsed_returns_the_mockups_own_worked_numbers()
    {
        // mockups/key-tariff-radar.html lines 276-314: current €12.50/mo + €0.3200/kWh, candidate
        // €14.90/mo + €0.3150/kWh + €350 switching bonus, pace 3,200 kWh/yr — "About €13/yr more,
        // not less" bonus-normalized line.
        var householdId = Guid.NewGuid();
        var current = CurrentTariff(householdId, 12.50m, 0.3200m, "EUR");
        _tariffRepository.FindCurrentForHouseholdAsync(householdId, Arg.Any<CancellationToken>()).Returns(current);
        _getCurrentStatus.ExecuteAsync(householdId, Arg.Any<CancellationToken>())
            .Returns(StatusAtElapsedDays(3200m, 365));
        var sut = Sut();

        var result = await sut.ExecuteAsync(householdId, 14.90m, 0.3150m, 350m, TestContext.Current.CancellationToken);

        result.ShouldNotBeNull();
        result.AnnualPaceKwh.ShouldBe(3200m);
        result.CurrentAnnualCost.ShouldBe(1174.00m);
        result.CandidateAnnualCostBonusNormalized.ShouldBe(1186.80m);
        result.BonusNormalizedAnnualSavings.ShouldBe(-12.80m);
    }

    [Fact]
    public async Task No_pace_available_from_GetCurrentStatus_returns_null()
    {
        var householdId = Guid.NewGuid();
        _tariffRepository.FindCurrentForHouseholdAsync(householdId, Arg.Any<CancellationToken>())
            .Returns(CurrentTariff(householdId, 12.50m, 0.32m, "EUR"));
        _getCurrentStatus.ExecuteAsync(householdId, Arg.Any<CancellationToken>()).Returns((CurrentStatusResult?)null);
        var sut = Sut();

        var result = await sut.ExecuteAsync(householdId, 14.90m, 0.3150m, 350m, TestContext.Current.CancellationToken);

        result.ShouldBeNull();
    }

    [Fact]
    public async Task No_current_Tariff_configured_returns_null()
    {
        var householdId = Guid.NewGuid();
        _tariffRepository.FindCurrentForHouseholdAsync(householdId, Arg.Any<CancellationToken>()).Returns((Tariff?)null);
        var sut = Sut();

        var result = await sut.ExecuteAsync(householdId, 14.90m, 0.3150m, 350m, TestContext.Current.CancellationToken);

        result.ShouldBeNull();
        // No pace lookup needed once there's nothing to compare against.
        await _getCurrentStatus.DidNotReceive().ExecuteAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task IsLowConfidence_is_propagated_from_GetCurrentStatus()
    {
        var householdId = Guid.NewGuid();
        _tariffRepository.FindCurrentForHouseholdAsync(householdId, Arg.Any<CancellationToken>())
            .Returns(CurrentTariff(householdId, 12.50m, 0.32m, "EUR"));
        _getCurrentStatus.ExecuteAsync(householdId, Arg.Any<CancellationToken>())
            .Returns(StatusAtElapsedDays(3200m, 365) with { IsLowConfidence = true });
        var sut = Sut();

        var result = await sut.ExecuteAsync(householdId, 14.90m, 0.3150m, 350m, TestContext.Current.CancellationToken);

        result.ShouldNotBeNull();
        result.IsLowConfidence.ShouldBeTrue();
    }

    [Fact]
    public async Task Partial_year_pace_is_correctly_annualized()
    {
        var householdId = Guid.NewGuid();
        _tariffRepository.FindCurrentForHouseholdAsync(householdId, Arg.Any<CancellationToken>())
            .Returns(CurrentTariff(householdId, 12.50m, 0.32m, "EUR"));
        // 200 days elapsed, 1000 kWh pace-to-date -> annualized = 1000 * 365 / 200 = 1825 kWh/yr.
        _getCurrentStatus.ExecuteAsync(householdId, Arg.Any<CancellationToken>())
            .Returns(StatusAtElapsedDays(1000m, 200));
        var sut = Sut();

        var result = await sut.ExecuteAsync(householdId, 14.90m, 0.3150m, 0m, TestContext.Current.CancellationToken);

        result.ShouldNotBeNull();
        result.AnnualPaceKwh.ShouldBe(1825m);
    }

    [Theory]
    [InlineData(-1, 0.32, 0)]
    [InlineData(10, 0, 0)]
    [InlineData(10, -0.32, 0)]
    [InlineData(10, 0.32, -1)]
    [InlineData(10, 0.32, 1_000_000_000_000_000)] // == TariffValidation.MaxMonthlyBaseFee, the switching bonus upper bound.
    public async Task Invalid_candidate_fields_throw_before_any_repository_or_status_lookup(
        decimal candidateMonthlyBaseFee, decimal candidatePricePerKwh, decimal candidateSwitchingBonus)
    {
        var householdId = Guid.NewGuid();
        var sut = Sut();

        await Should.ThrowAsync<TariffValidationException>(() =>
            sut.ExecuteAsync(householdId, candidateMonthlyBaseFee, candidatePricePerKwh, candidateSwitchingBonus, TestContext.Current.CancellationToken));

        await _tariffRepository.DidNotReceive().FindCurrentForHouseholdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
        await _getCurrentStatus.DidNotReceive().ExecuteAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }
}
