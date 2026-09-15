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
        // Story 5.3, FR-13: the mockup's own dramatization — the same candidate reads as a win
        // with the bonus and a loss without it. If a test run produces the same verdict for both
        // rows on this exact input, the bonus-included formula is wrong (most likely accidentally
        // routed through BonusDecayNormalizer).
        result.CandidateAnnualCostBonusIncluded.ShouldBe(836.80m);
        result.BonusIncludedAnnualSavings.ShouldBe(337.20m);
        result.IsBonusIncludedWorthSwitching.ShouldBeTrue();
        result.IsBonusNormalizedWorthSwitching.ShouldBeFalse();
    }

    [Fact]
    public async Task An_exact_breakeven_on_the_bonus_included_row_resolves_to_not_worth_switching_independently_of_the_normalized_row()
    {
        var householdId = Guid.NewGuid();
        var current = CurrentTariff(householdId, 12.50m, 0.3200m, "EUR");
        _tariffRepository.FindCurrentForHouseholdAsync(householdId, Arg.Any<CancellationToken>()).Returns(current);
        _getCurrentStatus.ExecuteAsync(householdId, Arg.Any<CancellationToken>())
            .Returns(StatusAtElapsedDays(3200m, 365));
        var sut = Sut();

        // currentAnnualCost = 1174.00, candidateAnnualCostNoBonus = 1186.80 -> a 12.80 switching
        // bonus makes candidateAnnualCostBonusIncluded exactly 1174.00 -> bonusIncludedAnnualSavings
        // == 0 (breakeven), while the normalized row (bonus fully decayed away at 365 days,
        // independent of the bonus amount) stays at its own non-zero -12.80.
        var result = await sut.ExecuteAsync(householdId, 14.90m, 0.3150m, 12.80m, TestContext.Current.CancellationToken);

        result.ShouldNotBeNull();
        result.BonusIncludedAnnualSavings.ShouldBe(0m);
        result.IsBonusIncludedWorthSwitching.ShouldBeFalse();
        result.BonusNormalizedAnnualSavings.ShouldNotBe(0m);
    }

    [Fact]
    public async Task An_exact_breakeven_on_the_bonus_normalized_row_resolves_to_not_worth_switching_independently_of_the_included_row()
    {
        var householdId = Guid.NewGuid();
        var current = CurrentTariff(householdId, 12.50m, 0.3200m, "EUR");
        _tariffRepository.FindCurrentForHouseholdAsync(householdId, Arg.Any<CancellationToken>()).Returns(current);
        _getCurrentStatus.ExecuteAsync(householdId, Arg.Any<CancellationToken>())
            .Returns(StatusAtElapsedDays(3200m, 365));
        var sut = Sut();

        // Candidate's base fee/price/kWh identical to the current Tariff -> candidateAnnualCostNoBonus
        // == currentAnnualCost -> bonusNormalizedAnnualSavings == 0 (breakeven, bonus fully decayed
        // away at 365 days). A non-zero switching bonus keeps the included row a distinct, non-zero
        // (worth-switching) figure.
        var result = await sut.ExecuteAsync(householdId, 12.50m, 0.3200m, 100m, TestContext.Current.CancellationToken);

        result.ShouldNotBeNull();
        result.BonusNormalizedAnnualSavings.ShouldBe(0m);
        result.IsBonusNormalizedWorthSwitching.ShouldBeFalse();
        result.BonusIncludedAnnualSavings.ShouldNotBe(0m);
    }

    [Fact]
    public async Task A_zero_switching_bonus_makes_the_bonus_included_and_bonus_normalized_costs_identical()
    {
        var householdId = Guid.NewGuid();
        var current = CurrentTariff(householdId, 12.50m, 0.3200m, "EUR");
        _tariffRepository.FindCurrentForHouseholdAsync(householdId, Arg.Any<CancellationToken>()).Returns(current);
        _getCurrentStatus.ExecuteAsync(householdId, Arg.Any<CancellationToken>())
            .Returns(StatusAtElapsedDays(3200m, 365));
        var sut = Sut();

        // No bonus -> nothing to normalize away -> both figures are numerically identical.
        var result = await sut.ExecuteAsync(householdId, 14.90m, 0.3150m, 0m, TestContext.Current.CancellationToken);

        result.ShouldNotBeNull();
        result.CandidateAnnualCostBonusIncluded.ShouldBe(result.CandidateAnnualCostBonusNormalized);
        result.BonusIncludedAnnualSavings.ShouldBe(result.BonusNormalizedAnnualSavings);
        result.IsBonusIncludedWorthSwitching.ShouldBe(result.IsBonusNormalizedWorthSwitching);
    }

    [Fact]
    public async Task Both_rows_resolve_to_worth_switching_when_the_candidate_wins_even_after_the_bonus_fully_decays()
    {
        // Dev Notes: "a candidate that's a win even after the bonus fully decays away... would show
        // both rows green" — one of three valid verdict combinations that must render correctly,
        // not just the mockup's own mixed (one green, one red) worked example.
        var householdId = Guid.NewGuid();
        var current = CurrentTariff(householdId, 20.00m, 0.4000m, "EUR");
        _tariffRepository.FindCurrentForHouseholdAsync(householdId, Arg.Any<CancellationToken>()).Returns(current);
        _getCurrentStatus.ExecuteAsync(householdId, Arg.Any<CancellationToken>())
            .Returns(StatusAtElapsedDays(3000m, 365));
        var sut = Sut();

        // currentAnnualCost = 1440.00, candidateAnnualCostNoBonus = 720.00 -> the candidate's own
        // ongoing rate already beats the current tariff, so both the bonus-included (620.00) and
        // bonus-normalized (720.00, bonus fully decayed) costs are worth switching.
        var result = await sut.ExecuteAsync(householdId, 10.00m, 0.2000m, 100m, TestContext.Current.CancellationToken);

        result.ShouldNotBeNull();
        result.IsBonusIncludedWorthSwitching.ShouldBeTrue();
        result.IsBonusNormalizedWorthSwitching.ShouldBeTrue();
    }

    [Fact]
    public async Task A_sub_cent_positive_savings_that_displays_as_0_00_resolves_to_not_worth_switching()
    {
        // Review round: the verdict is rounded to 2 decimals (matching the frontend's own money
        // display) before the tie-break, so a razor-thin positive savings can never show a
        // "Worth switching" badge next to a displayed "0.00" amount.
        var householdId = Guid.NewGuid();
        var current = CurrentTariff(householdId, 12.50m, 0.3200m, "EUR");
        _tariffRepository.FindCurrentForHouseholdAsync(householdId, Arg.Any<CancellationToken>()).Returns(current);
        _getCurrentStatus.ExecuteAsync(householdId, Arg.Any<CancellationToken>())
            .Returns(StatusAtElapsedDays(3200m, 365));
        var sut = Sut();

        // candidateAnnualCostNoBonus = 12.49975*12 + 0.3200*3200 = 1173.997 -> raw savings against
        // the 1174.00 current cost is exactly 0.003 (positive, but rounds to 0.00).
        var result = await sut.ExecuteAsync(householdId, 12.49975m, 0.3200m, 0m, TestContext.Current.CancellationToken);

        result.ShouldNotBeNull();
        result.BonusNormalizedAnnualSavings.ShouldBe(0.003m);
        result.IsBonusNormalizedWorthSwitching.ShouldBeFalse();
        result.BonusIncludedAnnualSavings.ShouldBe(0.003m);
        result.IsBonusIncludedWorthSwitching.ShouldBeFalse();
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
