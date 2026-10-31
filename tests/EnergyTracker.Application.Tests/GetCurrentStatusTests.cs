using EnergyTracker.Application.Ports;
using EnergyTracker.Domain;
using NSubstitute;
using Shouldly;

namespace EnergyTracker.Application.Tests;

public class GetCurrentStatusTests
{
    private readonly IHouseholdRepository _householdRepository = Substitute.For<IHouseholdRepository>();
    private readonly IMeterReadingRepository _readingRepository = Substitute.For<IMeterReadingRepository>();
    private readonly IMeterRegressionPromptRepository _regressionPromptRepository = Substitute.For<IMeterRegressionPromptRepository>();
    private readonly ISmartPlugCoverageSignal _smartPlugCoverageSignal = Substitute.For<ISmartPlugCoverageSignal>();

    private GetCurrentStatus Sut() => new(_householdRepository, _readingRepository, _regressionPromptRepository, _smartPlugCoverageSignal, TimeProvider.System);

    private static Household NewHousehold(Guid id, decimal? yearlyBaselineKwh, decimal trendingThresholdKwh = 100m, int lowConfidenceGapDays = 45) => new()
    {
        Id = id,
        Locale = "en-US",
        Currency = "USD",
        CreatedAtUtc = DateTimeOffset.UtcNow,
        YearlyBaselineKwh = yearlyBaselineKwh,
        TrendingThresholdKwh = trendingThresholdKwh,
        LowConfidenceGapDays = lowConfidenceGapDays,
    };

    private static MainMeter NewMainMeter(Guid householdId) => new()
    {
        Id = Guid.NewGuid(),
        HouseholdId = householdId,
        CreatedAtUtc = DateTimeOffset.UtcNow,
    };

    private static MeterReading NewReading(Guid householdId, Guid mainMeterId, decimal kwhValue, DateTimeOffset readingTimestamp) => new()
    {
        Id = Guid.NewGuid(),
        HouseholdId = householdId,
        MainMeterId = mainMeterId,
        KwhValue = kwhValue,
        ReadingTimestamp = readingTimestamp,
        IdempotencyKey = Guid.NewGuid(),
        CreatedAtUtc = DateTimeOffset.UtcNow,
    };

    public GetCurrentStatusTests()
    {
        _regressionPromptRepository.GetOpenForHouseholdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns((MeterRegressionPrompt?)null);
        _regressionPromptRepository.GetResolvedForMainMeterAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<MeterRegressionPrompt>)[]);
    }

    [Fact]
    public async Task No_Yearly_Baseline_set_is_undefined()
    {
        var householdId = Guid.NewGuid();
        _householdRepository.FindByIdAsync(householdId, Arg.Any<CancellationToken>()).Returns(NewHousehold(householdId, yearlyBaselineKwh: null));
        var sut = Sut();

        var result = await sut.ExecuteAsync(householdId, TestContext.Current.CancellationToken);

        result.ShouldBeNull();
        await _readingRepository.DidNotReceive().FindMainMeterByHouseholdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task No_MainMeter_yet_is_undefined()
    {
        var householdId = Guid.NewGuid();
        _householdRepository.FindByIdAsync(householdId, Arg.Any<CancellationToken>()).Returns(NewHousehold(householdId, yearlyBaselineKwh: 3650m));
        _readingRepository.FindMainMeterByHouseholdAsync(householdId, Arg.Any<CancellationToken>()).Returns((MainMeter?)null);
        var sut = Sut();

        var result = await sut.ExecuteAsync(householdId, TestContext.Current.CancellationToken);

        result.ShouldBeNull();
    }

    [Fact]
    public async Task Fewer_than_two_readings_is_undefined()
    {
        var householdId = Guid.NewGuid();
        var mainMeter = NewMainMeter(householdId);
        _householdRepository.FindByIdAsync(householdId, Arg.Any<CancellationToken>()).Returns(NewHousehold(householdId, yearlyBaselineKwh: 3650m));
        _readingRepository.FindMainMeterByHouseholdAsync(householdId, Arg.Any<CancellationToken>()).Returns(mainMeter);
        _readingRepository.GetRecentByMainMeterAsync(mainMeter.Id, Arg.Any<int>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns([NewReading(householdId, mainMeter.Id, 1000m, DateTimeOffset.UtcNow)]);
        var sut = Sut();

        var result = await sut.ExecuteAsync(householdId, TestContext.Current.CancellationToken);

        result.ShouldBeNull();
    }

    [Fact]
    public async Task Two_readings_within_the_trending_threshold_of_baseline_resolve_to_WithinRange()
    {
        var householdId = Guid.NewGuid();
        var mainMeter = NewMainMeter(householdId);
        var latest = DateTimeOffset.UtcNow;
        var baseline = latest.AddDays(-182.5);
        // Half a year elapsed; annual baseline 3650 kWh -> baseline-to-date 1825 kWh. Pace = 1825 (exact match).
        _householdRepository.FindByIdAsync(householdId, Arg.Any<CancellationToken>()).Returns(NewHousehold(householdId, yearlyBaselineKwh: 3650m));
        _readingRepository.FindMainMeterByHouseholdAsync(householdId, Arg.Any<CancellationToken>()).Returns(mainMeter);
        _readingRepository.GetRecentByMainMeterAsync(mainMeter.Id, Arg.Any<int>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>()).Returns(
        [
            NewReading(householdId, mainMeter.Id, 1000m, baseline),
            NewReading(householdId, mainMeter.Id, 2825m, latest),
        ]);
        var sut = Sut();

        var result = await sut.ExecuteAsync(householdId, TestContext.Current.CancellationToken);

        result.ShouldNotBeNull();
        result.Status.ShouldBe(Status.WithinRange);
        result.PaceToDateKwh.ShouldBe(1825m);
        result.ElapsedDays.ShouldBe(182.5, tolerance: 0.01);
        result.TrendingThresholdKwh.ShouldBe(100m);
        result.TrendingThresholdToDateKwh.ShouldBe(50m, 0.01m);
    }

    [Fact]
    public async Task Pace_exceeding_baseline_to_date_by_more_than_the_threshold_resolves_to_Trending()
    {
        var householdId = Guid.NewGuid();
        var mainMeter = NewMainMeter(householdId);
        var baseline = DateTimeOffset.UtcNow.AddDays(-182.5);
        var latest = DateTimeOffset.UtcNow;
        // baseline-to-date = 1825 kWh; threshold = 100 kWh; pace = 2000 kWh -> 175 over -> Trending.
        _householdRepository.FindByIdAsync(householdId, Arg.Any<CancellationToken>()).Returns(NewHousehold(householdId, yearlyBaselineKwh: 3650m));
        _readingRepository.FindMainMeterByHouseholdAsync(householdId, Arg.Any<CancellationToken>()).Returns(mainMeter);
        _readingRepository.GetRecentByMainMeterAsync(mainMeter.Id, Arg.Any<int>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>()).Returns(
        [
            NewReading(householdId, mainMeter.Id, 1000m, baseline),
            NewReading(householdId, mainMeter.Id, 3000m, latest),
        ]);
        var sut = Sut();

        var result = await sut.ExecuteAsync(householdId, TestContext.Current.CancellationToken);

        result.ShouldNotBeNull();
        result.Status.ShouldBe(Status.Trending);
    }

    // Story 11.15 (audit C12, FR-6). Fixed dates, not UtcNow arithmetic, so an exact tie is exact.
    // A yearly baseline of 3650 kWh is 10 kWh a day, so the baseline-to-date over N whole days is 10 x N.
    private async Task<CurrentStatusResult> StatusAfterAsync(
        decimal paceToDateKwh, TimeSpan elapsed, decimal annualThresholdKwh = 100m)
    {
        var householdId = Guid.NewGuid();
        var mainMeter = NewMainMeter(householdId);
        var start = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        _householdRepository.FindByIdAsync(householdId, Arg.Any<CancellationToken>())
            .Returns(NewHousehold(householdId, yearlyBaselineKwh: 3650m, trendingThresholdKwh: annualThresholdKwh));
        _readingRepository.FindMainMeterByHouseholdAsync(householdId, Arg.Any<CancellationToken>()).Returns(mainMeter);
        _readingRepository.GetRecentByMainMeterAsync(mainMeter.Id, Arg.Any<int>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>()).Returns(
        [
            NewReading(householdId, mainMeter.Id, 0m, start),
            NewReading(householdId, mainMeter.Id, paceToDateKwh, start + elapsed),
        ]);

        var result = await Sut().ExecuteAsync(householdId, TestContext.Current.CancellationToken);

        result.ShouldNotBeNull();
        return result;
    }

    [Fact]
    public async Task A_first_year_household_reaches_Trending_at_the_same_relative_overshoot_as_after_a_full_year()
    {
        // 73 days: baseline-to-date 730, pace 790, difference 60 against a scaled threshold of
        // 100 x 73/365 = 20. The pre-11.15 rule compared 60 with the unscaled 100 and said WithinRange.
        var result = await StatusAfterAsync(paceToDateKwh: 790m, TimeSpan.FromDays(73));

        result.Status.ShouldBe(Status.Trending);
        result.BaselineToDateKwh.ShouldBe(730m);
        result.TrendingThresholdKwh.ShouldBe(100m);
        result.TrendingThresholdToDateKwh.ShouldBe(20m);
    }

    // Review decision 1c (2026-10-25): the bar is never below one meter step (1 kWh). An annual 2 kWh
    // over 73 days prorates to 0.4; the shown and the deciding figure is the 1 kWh floor. (73 days keeps
    // the baseline-to-date, 730, exact in decimal so the tie is a real tie; 1/365 is a repeating decimal.)
    [Theory]
    [InlineData(731, Status.WithinRange)] // difference exactly 1 = the floor: the calmer state
    [InlineData(731.01, Status.Trending)]
    [InlineData(730.5, Status.WithinRange)] // under the floor, though over the prorated 0.4
    public async Task A_short_prorated_bar_is_floored_at_one_kWh_and_the_floored_figure_is_reported(decimal paceToDateKwh, Status expected)
    {
        var result = await StatusAfterAsync(paceToDateKwh, TimeSpan.FromDays(73), annualThresholdKwh: 2m);

        result.Status.ShouldBe(expected);
        result.TrendingThresholdKwh.ShouldBe(2m);
        result.TrendingThresholdToDateKwh.ShouldBe(1m);
    }

    [Theory]
    [InlineData(750, Status.WithinRange)] // difference exactly 20 = the scaled threshold: the calmer state
    [InlineData(750.01, Status.Trending)]
    public async Task The_scaled_threshold_is_strict_at_73_days(decimal paceToDateKwh, Status expected)
    {
        var result = await StatusAfterAsync(paceToDateKwh, TimeSpan.FromDays(73));

        result.Status.ShouldBe(expected);
    }

    [Theory]
    [InlineData(3750, Status.WithinRange)]
    [InlineData(3750.01, Status.Trending)]
    public async Task A_full_year_span_keeps_the_boundary_at_the_annual_threshold(decimal paceToDateKwh, Status expected)
    {
        var result = await StatusAfterAsync(paceToDateKwh, TimeSpan.FromDays(365));

        result.Status.ShouldBe(expected);
        result.BaselineToDateKwh.ShouldBe(3650m);
        result.TrendingThresholdToDateKwh.ShouldBe(100m);
    }

    [Theory]
    [InlineData(73, 729.99)]
    [InlineData(365, 3649.99)]
    public async Task Any_shortfall_below_the_baseline_to_date_is_BelowBaseline_at_every_span(double elapsedDays, decimal paceToDateKwh)
    {
        var result = await StatusAfterAsync(paceToDateKwh, TimeSpan.FromDays(elapsedDays));

        result.Status.ShouldBe(Status.BelowBaseline);
    }

    [Fact]
    public async Task The_scaled_threshold_shown_is_the_one_that_decides_the_Status()
    {
        // 100.5 days is not a clean fraction of 365, so the scaled threshold has many decimals. The
        // pace is built from the figures the result itself reports (AC #5): a difference equal to
        // the shown threshold is WithinRange, a hundredth above it is Trending.
        var span = TimeSpan.FromDays(100.5);
        var probe = await StatusAfterAsync(paceToDateKwh: 1m, span);
        var baselineToDateKwh = probe.BaselineToDateKwh;
        var shownThresholdKwh = probe.TrendingThresholdToDateKwh;

        var atThreshold = await StatusAfterAsync(baselineToDateKwh + shownThresholdKwh, span);
        var aboveThreshold = await StatusAfterAsync(baselineToDateKwh + shownThresholdKwh + 0.01m, span);
        var belowThreshold = await StatusAfterAsync(baselineToDateKwh + shownThresholdKwh - 0.01m, span);

        atThreshold.TrendingThresholdToDateKwh.ShouldBe(shownThresholdKwh);
        atThreshold.Status.ShouldBe(Status.WithinRange);
        aboveThreshold.Status.ShouldBe(Status.Trending);
        belowThreshold.Status.ShouldBe(Status.WithinRange);
    }

    [Fact]
    public async Task A_non_default_threshold_is_reported_per_year_and_scaled_to_the_span()
    {
        var result = await StatusAfterAsync(paceToDateKwh: 730m, TimeSpan.FromDays(73), annualThresholdKwh: 250m);

        result.TrendingThresholdKwh.ShouldBe(250m);
        result.TrendingThresholdToDateKwh.ShouldBe(50m); // 250 x 73/365
    }

    [Fact]
    public async Task An_open_regression_prompt_excludes_its_triggering_reading_and_everything_after_it()
    {
        var householdId = Guid.NewGuid();
        var mainMeter = NewMainMeter(householdId);
        var baseline = DateTimeOffset.UtcNow.AddDays(-30);
        var first = NewReading(householdId, mainMeter.Id, 1000m, baseline);
        var second = NewReading(householdId, mainMeter.Id, 1100m, baseline.AddDays(10));
        // A regression: lower than `second`, flagged and still open.
        var triggering = NewReading(householdId, mainMeter.Id, 50m, baseline.AddDays(20));
        var afterTriggering = NewReading(householdId, mainMeter.Id, 60m, baseline.AddDays(21));
        _householdRepository.FindByIdAsync(householdId, Arg.Any<CancellationToken>()).Returns(NewHousehold(householdId, yearlyBaselineKwh: 3650m));
        _readingRepository.FindMainMeterByHouseholdAsync(householdId, Arg.Any<CancellationToken>()).Returns(mainMeter);
        _readingRepository.GetRecentByMainMeterAsync(mainMeter.Id, Arg.Any<int>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns([first, second, triggering, afterTriggering]);
        _regressionPromptRepository.GetOpenForHouseholdAsync(householdId, Arg.Any<CancellationToken>()).Returns(
            new MeterRegressionPrompt
            {
                Id = Guid.NewGuid(),
                HouseholdId = householdId,
                MainMeterId = mainMeter.Id,
                MeterReadingId = triggering.Id,
                PreviousMeterReadingId = second.Id,
                CreatedAtUtc = DateTimeOffset.UtcNow,
            });
        var sut = Sut();

        var result = await sut.ExecuteAsync(householdId, TestContext.Current.CancellationToken);

        // Only `first` and `second` remain (100 kWh over 10 days) — `triggering` and
        // `afterTriggering` are excluded, so they must not contribute to PaceToDateKwh.
        result.ShouldNotBeNull();
        result.PaceToDateKwh.ShouldBe(100m);
    }

    [Fact]
    public async Task An_open_regression_prompt_makes_the_bounded_fetch_widen_on_the_previous_reading_not_the_trigger()
    {
        // Round-4 discipline: this asserts ONLY which arguments GetCurrentStatus passes to
        // GetRecentByMainMeterAsync for the widen scenario — it must never assert on the
        // *contents* of a mocked return value here, since that's exactly what let round 3's false
        // positive through (it mocked a result the real, buggy repository could never have
        // produced). Proving the real widen fetch's contents are correct is
        // MeterReadingRepositoryTests's job, against a real Testcontainers database.
        var householdId = Guid.NewGuid();
        var mainMeter = NewMainMeter(householdId);
        var baseline = DateTimeOffset.UtcNow.AddDays(-30);
        var first = NewReading(householdId, mainMeter.Id, 1000m, baseline);
        var second = NewReading(householdId, mainMeter.Id, 1100m, baseline.AddDays(10));
        var triggering = NewReading(householdId, mainMeter.Id, 50m, baseline.AddDays(20));
        _householdRepository.FindByIdAsync(householdId, Arg.Any<CancellationToken>()).Returns(NewHousehold(householdId, yearlyBaselineKwh: 3650m));
        _readingRepository.FindMainMeterByHouseholdAsync(householdId, Arg.Any<CancellationToken>()).Returns(mainMeter);
        _readingRepository.GetRecentByMainMeterAsync(mainMeter.Id, Arg.Any<int>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns([first, second, triggering]);
        _regressionPromptRepository.GetOpenForHouseholdAsync(householdId, Arg.Any<CancellationToken>()).Returns(
            new MeterRegressionPrompt
            {
                Id = Guid.NewGuid(),
                HouseholdId = householdId,
                MainMeterId = mainMeter.Id,
                MeterReadingId = triggering.Id,
                PreviousMeterReadingId = second.Id,
                CreatedAtUtc = DateTimeOffset.UtcNow,
            });
        var sut = Sut();

        await sut.ExecuteAsync(householdId, TestContext.Current.CancellationToken);

        // Anchored on PreviousMeterReadingId (`second.Id`) — never MeterReadingId (`triggering.Id`,
        // round 2's bug) — and windowDays is the shared 400-day constant, not a magic number
        // re-literaled at the call site.
        await _readingRepository.Received(1).GetRecentByMainMeterAsync(mainMeter.Id, 400, second.Id, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task With_no_open_prompt_the_bounded_fetch_is_called_with_a_null_must_include_id()
    {
        var householdId = Guid.NewGuid();
        var mainMeter = NewMainMeter(householdId);
        _householdRepository.FindByIdAsync(householdId, Arg.Any<CancellationToken>()).Returns(NewHousehold(householdId, yearlyBaselineKwh: 3650m));
        _readingRepository.FindMainMeterByHouseholdAsync(householdId, Arg.Any<CancellationToken>()).Returns(mainMeter);
        _readingRepository.GetRecentByMainMeterAsync(mainMeter.Id, Arg.Any<int>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns([NewReading(householdId, mainMeter.Id, 1000m, DateTimeOffset.UtcNow.AddDays(-10)), NewReading(householdId, mainMeter.Id, 1100m, DateTimeOffset.UtcNow)]);
        var sut = Sut();

        await sut.ExecuteAsync(householdId, TestContext.Current.CancellationToken);

        await _readingRepository.Received(1).GetRecentByMainMeterAsync(mainMeter.Id, 400, null, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task An_open_regression_prompt_that_leaves_fewer_than_two_readings_makes_Status_undefined()
    {
        var householdId = Guid.NewGuid();
        var mainMeter = NewMainMeter(householdId);
        var first = NewReading(householdId, mainMeter.Id, 1000m, DateTimeOffset.UtcNow.AddDays(-10));
        var triggering = NewReading(householdId, mainMeter.Id, 50m, DateTimeOffset.UtcNow);
        _householdRepository.FindByIdAsync(householdId, Arg.Any<CancellationToken>()).Returns(NewHousehold(householdId, yearlyBaselineKwh: 3650m));
        _readingRepository.FindMainMeterByHouseholdAsync(householdId, Arg.Any<CancellationToken>()).Returns(mainMeter);
        _readingRepository.GetRecentByMainMeterAsync(mainMeter.Id, Arg.Any<int>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>()).Returns([first, triggering]);
        _regressionPromptRepository.GetOpenForHouseholdAsync(householdId, Arg.Any<CancellationToken>()).Returns(
            new MeterRegressionPrompt
            {
                Id = Guid.NewGuid(),
                HouseholdId = householdId,
                MainMeterId = mainMeter.Id,
                MeterReadingId = triggering.Id,
                PreviousMeterReadingId = first.Id,
                CreatedAtUtc = DateTimeOffset.UtcNow,
            });
        var sut = Sut();

        var result = await sut.ExecuteAsync(householdId, TestContext.Current.CancellationToken);

        result.ShouldBeNull();
    }

    [Fact]
    public async Task A_gap_since_the_last_reading_longer_than_the_households_LowConfidenceGapDays_is_flagged()
    {
        var householdId = Guid.NewGuid();
        var mainMeter = NewMainMeter(householdId);
        var first = NewReading(householdId, mainMeter.Id, 1000m, DateTimeOffset.UtcNow.AddDays(-100));
        var last = NewReading(householdId, mainMeter.Id, 1100m, DateTimeOffset.UtcNow.AddDays(-50));
        _householdRepository.FindByIdAsync(householdId, Arg.Any<CancellationToken>())
            .Returns(NewHousehold(householdId, yearlyBaselineKwh: 3650m, lowConfidenceGapDays: 45));
        _readingRepository.FindMainMeterByHouseholdAsync(householdId, Arg.Any<CancellationToken>()).Returns(mainMeter);
        _readingRepository.GetRecentByMainMeterAsync(mainMeter.Id, Arg.Any<int>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>()).Returns([first, last]);
        var sut = Sut();

        var result = await sut.ExecuteAsync(householdId, TestContext.Current.CancellationToken);

        result.ShouldNotBeNull();
        result.IsLowConfidence.ShouldBeTrue();
        result.DaysSinceLastReading.ShouldBe(50, tolerance: 0.1);
        result.LowConfidenceGapDaysThreshold.ShouldBe(45);
    }

    [Fact]
    public async Task A_recent_last_reading_within_the_LowConfidenceGapDays_window_is_not_flagged()
    {
        var householdId = Guid.NewGuid();
        var mainMeter = NewMainMeter(householdId);
        var first = NewReading(householdId, mainMeter.Id, 1000m, DateTimeOffset.UtcNow.AddDays(-10));
        var last = NewReading(householdId, mainMeter.Id, 1100m, DateTimeOffset.UtcNow.AddDays(-1));
        _householdRepository.FindByIdAsync(householdId, Arg.Any<CancellationToken>())
            .Returns(NewHousehold(householdId, yearlyBaselineKwh: 3650m, lowConfidenceGapDays: 45));
        _readingRepository.FindMainMeterByHouseholdAsync(householdId, Arg.Any<CancellationToken>()).Returns(mainMeter);
        _readingRepository.GetRecentByMainMeterAsync(mainMeter.Id, Arg.Any<int>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>()).Returns([first, last]);
        var sut = Sut();

        var result = await sut.ExecuteAsync(householdId, TestContext.Current.CancellationToken);

        result.ShouldNotBeNull();
        result.IsLowConfidence.ShouldBeFalse();
    }

    [Fact]
    public async Task A_resolved_rollover_regression_is_corrected_using_the_prompts_digit_capacity_instead_of_the_raw_negative_delta()
    {
        var householdId = Guid.NewGuid();
        var mainMeter = NewMainMeter(householdId);
        var first = NewReading(householdId, mainMeter.Id, 9990m, DateTimeOffset.UtcNow.AddDays(-10));
        // The meter rolled over: raw delta (10 - 9990) would be a large negative number.
        var afterRollover = NewReading(householdId, mainMeter.Id, 10m, DateTimeOffset.UtcNow);
        _householdRepository.FindByIdAsync(householdId, Arg.Any<CancellationToken>()).Returns(NewHousehold(householdId, yearlyBaselineKwh: 3650m));
        _readingRepository.FindMainMeterByHouseholdAsync(householdId, Arg.Any<CancellationToken>()).Returns(mainMeter);
        _readingRepository.GetRecentByMainMeterAsync(mainMeter.Id, Arg.Any<int>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>()).Returns([first, afterRollover]);
        _regressionPromptRepository.GetResolvedForMainMeterAsync(mainMeter.Id, Arg.Any<CancellationToken>()).Returns(
        [
            new MeterRegressionPrompt
            {
                Id = Guid.NewGuid(),
                HouseholdId = householdId,
                MainMeterId = mainMeter.Id,
                MeterReadingId = afterRollover.Id,
                PreviousMeterReadingId = first.Id,
                CreatedAtUtc = DateTimeOffset.UtcNow,
                Classification = MeterRegressionClassification.Rollover,
                DigitCapacityKwh = 10000m,
                ResolvedAtUtc = DateTimeOffset.UtcNow,
            },
        ]);
        var sut = Sut();

        var result = await sut.ExecuteAsync(householdId, TestContext.Current.CancellationToken);

        // (10000 - 9990) + 10 = 20 kWh, not the raw (10 - 9990) = -9980 kWh.
        result.ShouldNotBeNull();
        result.PaceToDateKwh.ShouldBe(20m);
    }

    [Fact]
    public async Task A_resolved_reset_regression_voids_the_spanning_pair_instead_of_contributing_a_negative_delta()
    {
        var householdId = Guid.NewGuid();
        var mainMeter = NewMainMeter(householdId);
        var first = NewReading(householdId, mainMeter.Id, 9990m, DateTimeOffset.UtcNow.AddDays(-20));
        // The meter was physically replaced/reset — old and new cumulative totals aren't comparable.
        var afterReset = NewReading(householdId, mainMeter.Id, 10m, DateTimeOffset.UtcNow.AddDays(-10));
        var latest = NewReading(householdId, mainMeter.Id, 30m, DateTimeOffset.UtcNow);
        _householdRepository.FindByIdAsync(householdId, Arg.Any<CancellationToken>()).Returns(NewHousehold(householdId, yearlyBaselineKwh: 3650m));
        _readingRepository.FindMainMeterByHouseholdAsync(householdId, Arg.Any<CancellationToken>()).Returns(mainMeter);
        _readingRepository.GetRecentByMainMeterAsync(mainMeter.Id, Arg.Any<int>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>()).Returns([first, afterReset, latest]);
        _regressionPromptRepository.GetResolvedForMainMeterAsync(mainMeter.Id, Arg.Any<CancellationToken>()).Returns(
        [
            new MeterRegressionPrompt
            {
                Id = Guid.NewGuid(),
                HouseholdId = householdId,
                MainMeterId = mainMeter.Id,
                MeterReadingId = afterReset.Id,
                PreviousMeterReadingId = first.Id,
                CreatedAtUtc = DateTimeOffset.UtcNow,
                Classification = MeterRegressionClassification.Reset,
                ResolvedAtUtc = DateTimeOffset.UtcNow,
            },
        ]);
        var sut = Sut();

        var result = await sut.ExecuteAsync(householdId, TestContext.Current.CancellationToken);

        // The (first, afterReset) pair is voided entirely; only (afterReset, latest) = 20 kWh over 10 days counts.
        result.ShouldNotBeNull();
        result.PaceToDateKwh.ShouldBe(20m);
    }

    [Fact]
    public async Task Readings_older_than_a_trailing_year_from_the_latest_reading_dont_dilute_pace_to_date()
    {
        var householdId = Guid.NewGuid();
        var mainMeter = NewMainMeter(householdId);
        var latest = DateTimeOffset.UtcNow;
        // Outside the trailing-365-day window from `latest` — must not contribute.
        var ancient = NewReading(householdId, mainMeter.Id, 0m, latest.AddDays(-400));
        var windowStart = NewReading(householdId, mainMeter.Id, 1000m, latest.AddDays(-182.5));
        var recent = NewReading(householdId, mainMeter.Id, 2825m, latest);
        _householdRepository.FindByIdAsync(householdId, Arg.Any<CancellationToken>()).Returns(NewHousehold(householdId, yearlyBaselineKwh: 3650m));
        _readingRepository.FindMainMeterByHouseholdAsync(householdId, Arg.Any<CancellationToken>()).Returns(mainMeter);
        _readingRepository.GetRecentByMainMeterAsync(mainMeter.Id, Arg.Any<int>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>()).Returns([ancient, windowStart, recent]);
        var sut = Sut();

        var result = await sut.ExecuteAsync(householdId, TestContext.Current.CancellationToken);

        // Same 1825 kWh / half-year figures as the unwindowed WithinRange test — `ancient` is excluded.
        result.ShouldNotBeNull();
        result.Status.ShouldBe(Status.WithinRange);
        result.PaceToDateKwh.ShouldBe(1825m);
    }

    [Fact]
    public async Task An_idle_household_whose_last_reading_is_500_days_old_still_computes_the_same_result_as_an_unbounded_fetch()
    {
        // I/O matrix: "Idle household, bounded fetch" — the bounded window is relative to the
        // MainMeter's own most recent reading, never DateTimeOffset.UtcNow, so a household that
        // hasn't logged anything in 500 days must still resolve a defined Status identical to what
        // an unbounded fetch would have produced (Design Notes: "why bound from the last reading,
        // not from now").
        var householdId = Guid.NewGuid();
        var mainMeter = NewMainMeter(householdId);
        var latest = DateTimeOffset.UtcNow.AddDays(-500);
        var baseline = latest.AddDays(-182.5);
        _householdRepository.FindByIdAsync(householdId, Arg.Any<CancellationToken>()).Returns(NewHousehold(householdId, yearlyBaselineKwh: 3650m));
        _readingRepository.FindMainMeterByHouseholdAsync(householdId, Arg.Any<CancellationToken>()).Returns(mainMeter);
        _readingRepository.GetRecentByMainMeterAsync(mainMeter.Id, Arg.Any<int>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>()).Returns(
        [
            NewReading(householdId, mainMeter.Id, 1000m, baseline),
            NewReading(householdId, mainMeter.Id, 2825m, latest),
        ]);
        var sut = Sut();

        var result = await sut.ExecuteAsync(householdId, TestContext.Current.CancellationToken);

        // Same 1825 kWh / half-year figures as the unwindowed WithinRange test — only the
        // readings' own timestamps (not how long ago "now" is) drive the computation.
        result.ShouldNotBeNull();
        result.Status.ShouldBe(Status.WithinRange);
        result.PaceToDateKwh.ShouldBe(1825m);
    }

    [Fact]
    public async Task A_household_with_5_plus_years_of_history_matches_what_an_unbounded_fetch_would_produce()
    {
        // I/O matrix: "Long-lived household, bounded fetch" — only the trailing window
        // contributes; a Main Meter with several years of readings behind it must resolve
        // identically to a hypothetical unbounded fetch of its full history.
        var householdId = Guid.NewGuid();
        var mainMeter = NewMainMeter(householdId);
        var latest = DateTimeOffset.UtcNow;
        // Over 5 years before `latest` — nowhere near the 400-day window, must not contribute.
        var fiveYearsAgo = NewReading(householdId, mainMeter.Id, -50000m, latest.AddYears(-5));
        var twoYearsAgo = NewReading(householdId, mainMeter.Id, 0m, latest.AddYears(-2));
        var windowStart = NewReading(householdId, mainMeter.Id, 1000m, latest.AddDays(-182.5));
        var recent = NewReading(householdId, mainMeter.Id, 2825m, latest);
        _householdRepository.FindByIdAsync(householdId, Arg.Any<CancellationToken>()).Returns(NewHousehold(householdId, yearlyBaselineKwh: 3650m));
        _readingRepository.FindMainMeterByHouseholdAsync(householdId, Arg.Any<CancellationToken>()).Returns(mainMeter);
        _readingRepository.GetRecentByMainMeterAsync(mainMeter.Id, Arg.Any<int>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns([fiveYearsAgo, twoYearsAgo, windowStart, recent]);
        var sut = Sut();

        var result = await sut.ExecuteAsync(householdId, TestContext.Current.CancellationToken);

        result.ShouldNotBeNull();
        result.Status.ShouldBe(Status.WithinRange);
        result.PaceToDateKwh.ShouldBe(1825m);
        result.BaselineToDateKwh.ShouldBe(1825m);
    }

    [Fact]
    public async Task A_long_gap_corroborated_by_Smart_Plug_coverage_downgrades_IsLowConfidence_to_false()
    {
        var householdId = Guid.NewGuid();
        var mainMeter = NewMainMeter(householdId);
        var first = NewReading(householdId, mainMeter.Id, 1000m, DateTimeOffset.UtcNow.AddDays(-100));
        var last = NewReading(householdId, mainMeter.Id, 1100m, DateTimeOffset.UtcNow.AddDays(-50));
        _householdRepository.FindByIdAsync(householdId, Arg.Any<CancellationToken>())
            .Returns(NewHousehold(householdId, yearlyBaselineKwh: 3650m, lowConfidenceGapDays: 45));
        _readingRepository.FindMainMeterByHouseholdAsync(householdId, Arg.Any<CancellationToken>()).Returns(mainMeter);
        _readingRepository.GetRecentByMainMeterAsync(mainMeter.Id, Arg.Any<int>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>()).Returns([first, last]);
        _smartPlugCoverageSignal.HasCoverageDuringAsync(householdId, last.ReadingTimestamp, Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(true);
        var sut = Sut();

        var result = await sut.ExecuteAsync(householdId, TestContext.Current.CancellationToken);

        result.ShouldNotBeNull();
        result.IsLowConfidence.ShouldBeFalse();
    }

    [Fact]
    public async Task Smart_Plug_corroboration_of_a_low_confidence_gap_never_changes_Pace_Baseline_or_Status_AD_14()
    {
        // Combines both guards Task 7 asks for in one test: a low-confidence gap *with* active
        // Smart-Plug corroboration must still leave PaceToDateKwh/BaselineToDateKwh/Status exactly
        // as they'd be computed from Meter Readings alone — corroboration may only ever soften
        // IsLowConfidence, never touch anything else (AC #2, AD-14). Asserted by comparing the
        // corroborated and uncorroborated results directly rather than hardcoding expected
        // pace/baseline figures, so this doesn't silently drift if PatternDetectiveCalculator's
        // formula ever changes.
        var householdId = Guid.NewGuid();
        var mainMeter = NewMainMeter(householdId);
        var first = NewReading(householdId, mainMeter.Id, 1000m, DateTimeOffset.UtcNow.AddDays(-100));
        var last = NewReading(householdId, mainMeter.Id, 1100m, DateTimeOffset.UtcNow.AddDays(-50));
        _householdRepository.FindByIdAsync(householdId, Arg.Any<CancellationToken>())
            .Returns(NewHousehold(householdId, yearlyBaselineKwh: 3650m, lowConfidenceGapDays: 45));
        _readingRepository.FindMainMeterByHouseholdAsync(householdId, Arg.Any<CancellationToken>()).Returns(mainMeter);
        _readingRepository.GetRecentByMainMeterAsync(mainMeter.Id, Arg.Any<int>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>()).Returns([first, last]);

        _smartPlugCoverageSignal.HasCoverageDuringAsync(Arg.Any<Guid>(), Arg.Any<DateTimeOffset>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(false);
        var uncorroborated = await Sut().ExecuteAsync(householdId, TestContext.Current.CancellationToken);

        _smartPlugCoverageSignal.HasCoverageDuringAsync(Arg.Any<Guid>(), Arg.Any<DateTimeOffset>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(true);
        var corroborated = await Sut().ExecuteAsync(householdId, TestContext.Current.CancellationToken);

        uncorroborated.ShouldNotBeNull();
        corroborated.ShouldNotBeNull();
        uncorroborated!.IsLowConfidence.ShouldBeTrue();
        corroborated!.IsLowConfidence.ShouldBeFalse();
        corroborated.PaceToDateKwh.ShouldBe(uncorroborated.PaceToDateKwh);
        corroborated.BaselineToDateKwh.ShouldBe(uncorroborated.BaselineToDateKwh);
        corroborated.Status.ShouldBe(uncorroborated.Status);
    }

    [Fact]
    public async Task A_long_gap_with_zero_Smart_Plug_coverage_leaves_IsLowConfidence_unchanged_true()
    {
        // AC #1 regression guard: a Household with zero Smart Plug coverage must still get a
        // fully functional Status computed from Meter Readings alone.
        var householdId = Guid.NewGuid();
        var mainMeter = NewMainMeter(householdId);
        var first = NewReading(householdId, mainMeter.Id, 1000m, DateTimeOffset.UtcNow.AddDays(-100));
        var last = NewReading(householdId, mainMeter.Id, 1100m, DateTimeOffset.UtcNow.AddDays(-50));
        _householdRepository.FindByIdAsync(householdId, Arg.Any<CancellationToken>())
            .Returns(NewHousehold(householdId, yearlyBaselineKwh: 3650m, lowConfidenceGapDays: 45));
        _readingRepository.FindMainMeterByHouseholdAsync(householdId, Arg.Any<CancellationToken>()).Returns(mainMeter);
        _readingRepository.GetRecentByMainMeterAsync(mainMeter.Id, Arg.Any<int>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>()).Returns([first, last]);
        _smartPlugCoverageSignal.HasCoverageDuringAsync(Arg.Any<Guid>(), Arg.Any<DateTimeOffset>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(false);
        var sut = Sut();

        var result = await sut.ExecuteAsync(householdId, TestContext.Current.CancellationToken);

        result.ShouldNotBeNull();
        result.IsLowConfidence.ShouldBeTrue();
    }

    [Fact]
    public async Task Smart_Plug_coverage_never_affects_PaceToDateKwh_BaselineToDateKwh_or_Status_AD_14()
    {
        // AC #2/AD-14 regression guard: Smart Plug data may only ever soften IsLowConfidence —
        // it must never be summed against or reconcile the pace/baseline/Trending figures.
        var householdId = Guid.NewGuid();
        var mainMeter = NewMainMeter(householdId);
        var latest = DateTimeOffset.UtcNow;
        var baseline = latest.AddDays(-182.5);
        _householdRepository.FindByIdAsync(householdId, Arg.Any<CancellationToken>()).Returns(NewHousehold(householdId, yearlyBaselineKwh: 3650m));
        _readingRepository.FindMainMeterByHouseholdAsync(householdId, Arg.Any<CancellationToken>()).Returns(mainMeter);
        _readingRepository.GetRecentByMainMeterAsync(mainMeter.Id, Arg.Any<int>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>()).Returns(
        [
            NewReading(householdId, mainMeter.Id, 1000m, baseline),
            NewReading(householdId, mainMeter.Id, 2825m, latest),
        ]);
        _smartPlugCoverageSignal.HasCoverageDuringAsync(Arg.Any<Guid>(), Arg.Any<DateTimeOffset>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(true);
        var sut = Sut();

        var result = await sut.ExecuteAsync(householdId, TestContext.Current.CancellationToken);

        // Recent reading — never low-confidence in the first place, so the coverage signal isn't
        // even consulted; PaceToDateKwh/BaselineToDateKwh/Status are unaffected either way.
        result.ShouldNotBeNull();
        result.Status.ShouldBe(Status.WithinRange);
        result.PaceToDateKwh.ShouldBe(1825m);
        result.BaselineToDateKwh.ShouldBe(1825m);
        await _smartPlugCoverageSignal.DidNotReceive().HasCoverageDuringAsync(
            Arg.Any<Guid>(), Arg.Any<DateTimeOffset>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    public async Task The_gap_to_the_last_reading_is_measured_against_the_injected_clock_and_flags_only_beyond_LowConfidenceGapDays(
        int ticksPastTheLimit, bool expectedLowConfidence)
    {
        var householdId = Guid.NewGuid();
        var mainMeter = NewMainMeter(householdId);
        var clock = new ManualTimeProvider();
        var first = clock.GetUtcNow().AddDays(-60);
        var last = clock.GetUtcNow().AddDays(-50);
        clock.Set(last.AddDays(45).AddTicks(ticksPastTheLimit));
        _householdRepository.FindByIdAsync(householdId, Arg.Any<CancellationToken>()).Returns(NewHousehold(householdId, yearlyBaselineKwh: 3650m, lowConfidenceGapDays: 45));
        _readingRepository.FindMainMeterByHouseholdAsync(householdId, Arg.Any<CancellationToken>()).Returns(mainMeter);
        _readingRepository.GetRecentByMainMeterAsync(mainMeter.Id, Arg.Any<int>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>()).Returns(
        [
            NewReading(householdId, mainMeter.Id, 1000m, first),
            NewReading(householdId, mainMeter.Id, 1100m, last),
        ]);
        var sut = new GetCurrentStatus(_householdRepository, _readingRepository, _regressionPromptRepository, _smartPlugCoverageSignal, clock);

        var result = await sut.ExecuteAsync(householdId, TestContext.Current.CancellationToken);

        result.ShouldNotBeNull();
        result.IsLowConfidence.ShouldBe(expectedLowConfidence);
        result.ElapsedDays.ShouldBe(10, tolerance: 0.01);
    }
}
