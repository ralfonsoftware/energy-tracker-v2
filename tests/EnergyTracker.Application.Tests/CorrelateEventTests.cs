using EnergyTracker.Application.Ports;
using EnergyTracker.Domain;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Shouldly;

namespace EnergyTracker.Application.Tests;

public class CorrelateEventTests
{
    private readonly IHouseholdRepository _householdRepository = Substitute.For<IHouseholdRepository>();
    private readonly IMeterReadingRepository _readingRepository = Substitute.For<IMeterReadingRepository>();
    private readonly IMeterRegressionPromptRepository _regressionPromptRepository = Substitute.For<IMeterRegressionPromptRepository>();
    private readonly IEventRepository _eventRepository = Substitute.For<IEventRepository>();
    private readonly IAiPlausibilityClient _aiPlausibilityClient = Substitute.For<IAiPlausibilityClient>();
    private readonly ILogger<CorrelateEvent> _logger = Substitute.For<ILogger<CorrelateEvent>>();

    public CorrelateEventTests()
    {
        _regressionPromptRepository.GetResolvedForMainMeterAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<MeterRegressionPrompt>)[]);
    }

    private Event SeedEvent(Guid householdId, string? persistedDirection = null)
    {
        var @event = new Event
        {
            Id = Guid.NewGuid(),
            HouseholdId = householdId,
            Description = "gaming session 3h",
            OccurredAt = OccurredAt,
            CreatedAtUtc = DateTimeOffset.UtcNow,
            CorrelationDirection = persistedDirection,
            CorrelationComputedAtUtc = persistedDirection is null ? null : DateTimeOffset.UtcNow,
        };
        _eventRepository.FindByIdAsync(@event.Id, Arg.Any<CancellationToken>()).Returns(@event);
        return @event;
    }

    private CorrelateEvent Sut() => new(_householdRepository, _readingRepository, _regressionPromptRepository, _eventRepository, _aiPlausibilityClient, _logger);

    private static readonly DateTimeOffset OccurredAt = new(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);

    private static Household NewHousehold(Guid householdId, bool aiPlausibilityEnabled, decimal? yearlyBaselineKwh = 3650m) => new()
    {
        Id = householdId,
        Locale = "en-US",
        Currency = "USD",
        CreatedAtUtc = DateTimeOffset.UtcNow,
        AiPlausibilityEnabled = aiPlausibilityEnabled,
        YearlyBaselineKwh = yearlyBaselineKwh,
        TrendingThresholdKwh = 100m,
    };

    private static MeterReading Reading(Guid mainMeterId, decimal kwhValue, DateTimeOffset readingTimestamp) => new()
    {
        Id = Guid.NewGuid(),
        HouseholdId = Guid.NewGuid(),
        MainMeterId = mainMeterId,
        KwhValue = kwhValue,
        ReadingTimestamp = readingTimestamp,
        IdempotencyKey = Guid.NewGuid(),
        CreatedAtUtc = DateTimeOffset.UtcNow,
    };

    // Actual delta (1000 kWh) is an order of magnitude above the ~140 kWh expected over 14 days —
    // margin generous enough that decimal-proration rounding never matters at this layer (that
    // precision is WindowedDeviationCalculatorTests's job, not this one's).
    private MainMeter SeedMainMeterWithABump(Guid householdId)
    {
        var mainMeter = new MainMeter { Id = Guid.NewGuid(), HouseholdId = householdId, CreatedAtUtc = DateTimeOffset.UtcNow };
        _readingRepository.FindMainMeterByHouseholdAsync(householdId, Arg.Any<CancellationToken>()).Returns(mainMeter);
        _readingRepository.GetInWindowByMainMeterAsync(mainMeter.Id, Arg.Any<DateTimeOffset>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<MeterReading>)
            [
                Reading(mainMeter.Id, 1000m, OccurredAt - TimeSpan.FromDays(7)),
                Reading(mainMeter.Id, 2000m, OccurredAt + TimeSpan.FromDays(7)),
            ]);
        return mainMeter;
    }

    private CorrelateEventPayload Payload(Guid householdId, Guid eventId) =>
        new(eventId, householdId, OccurredAt, "gaming session 3h");

    [Fact]
    public async Task Disabled_household_does_nothing()
    {
        var householdId = Guid.NewGuid();
        _householdRepository.FindByIdAsync(householdId, Arg.Any<CancellationToken>())
            .Returns(NewHousehold(householdId, aiPlausibilityEnabled: false));
        var sut = Sut();

        await sut.ExecuteAsync(householdId, Payload(householdId, Guid.NewGuid()), TestContext.Current.CancellationToken);

        await _eventRepository.DidNotReceive().SetCorrelationAsync(
            Arg.Any<Guid>(), Arg.Any<string?>(), Arg.Any<DateTimeOffset?>(), Arg.Any<CancellationToken>());
        await _aiPlausibilityClient.DidNotReceive().ClassifyAsync(
            Arg.Any<string>(), Arg.Any<IReadOnlyCollection<AiPlausibilityDirection>>(), Arg.Any<CancellationToken>());
    }

    // Represents the "unconfigured" case too: an unconfigured deployment resolves
    // IAiPlausibilityClient to NoOpAiPlausibilityClient, which always returns null — from
    // CorrelateEvent's perspective that's indistinguishable from a configured client finding no
    // match, exercised by Deviation_with_no_AI_match_leaves_correlation_null below.
    [Fact]
    public async Task Household_with_no_YearlyBaseline_set_does_nothing()
    {
        var householdId = Guid.NewGuid();
        _householdRepository.FindByIdAsync(householdId, Arg.Any<CancellationToken>())
            .Returns(NewHousehold(householdId, aiPlausibilityEnabled: true, yearlyBaselineKwh: null));
        var sut = Sut();

        await sut.ExecuteAsync(householdId, Payload(householdId, Guid.NewGuid()), TestContext.Current.CancellationToken);

        await _readingRepository.DidNotReceive().FindMainMeterByHouseholdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
        await _eventRepository.DidNotReceive().SetCorrelationAsync(
            Arg.Any<Guid>(), Arg.Any<string?>(), Arg.Any<DateTimeOffset?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task No_MainMeter_yet_does_nothing()
    {
        var householdId = Guid.NewGuid();
        _householdRepository.FindByIdAsync(householdId, Arg.Any<CancellationToken>())
            .Returns(NewHousehold(householdId, aiPlausibilityEnabled: true));
        _readingRepository.FindMainMeterByHouseholdAsync(householdId, Arg.Any<CancellationToken>()).Returns((MainMeter?)null);
        var sut = Sut();

        await sut.ExecuteAsync(householdId, Payload(householdId, Guid.NewGuid()), TestContext.Current.CancellationToken);

        await _eventRepository.DidNotReceive().SetCorrelationAsync(
            Arg.Any<Guid>(), Arg.Any<string?>(), Arg.Any<DateTimeOffset?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Enabled_with_no_observable_deviation_leaves_the_correlation_null()
    {
        var householdId = Guid.NewGuid();
        _householdRepository.FindByIdAsync(householdId, Arg.Any<CancellationToken>())
            .Returns(NewHousehold(householdId, aiPlausibilityEnabled: true));
        var mainMeter = new MainMeter { Id = Guid.NewGuid(), HouseholdId = householdId, CreatedAtUtc = DateTimeOffset.UtcNow };
        _readingRepository.FindMainMeterByHouseholdAsync(householdId, Arg.Any<CancellationToken>()).Returns(mainMeter);
        // Only one reading in the window — WindowedDeviationCalculator returns null for that.
        _readingRepository.GetInWindowByMainMeterAsync(mainMeter.Id, Arg.Any<DateTimeOffset>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<MeterReading>)[Reading(mainMeter.Id, 1000m, OccurredAt)]);
        var sut = Sut();

        await sut.ExecuteAsync(householdId, Payload(householdId, SeedEvent(householdId).Id), TestContext.Current.CancellationToken);

        await _aiPlausibilityClient.DidNotReceive().ClassifyAsync(
            Arg.Any<string>(), Arg.Any<IReadOnlyCollection<AiPlausibilityDirection>>(), Arg.Any<CancellationToken>());
        await _eventRepository.DidNotReceive().SetCorrelationAsync(
            Arg.Any<Guid>(), Arg.Any<string?>(), Arg.Any<DateTimeOffset?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Enabled_with_a_deviation_and_an_AI_match_persists_the_correlation()
    {
        var householdId = Guid.NewGuid();
        var eventId = SeedEvent(householdId).Id;
        _householdRepository.FindByIdAsync(householdId, Arg.Any<CancellationToken>())
            .Returns(NewHousehold(householdId, aiPlausibilityEnabled: true));
        SeedMainMeterWithABump(householdId);
        _aiPlausibilityClient.ClassifyAsync(
                "gaming session 3h", Arg.Is<IReadOnlyCollection<AiPlausibilityDirection>>(d => d.Contains(AiPlausibilityDirection.Bump)),
                Arg.Any<CancellationToken>())
            .Returns(AiPlausibilityDirection.Bump);
        var sut = Sut();

        await sut.ExecuteAsync(householdId, Payload(householdId, eventId), TestContext.Current.CancellationToken);

        await _eventRepository.Received(1).SetCorrelationAsync(eventId, "Bump", Arg.Any<DateTimeOffset?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Enabled_with_a_deviation_but_no_AI_match_leaves_the_correlation_null()
    {
        var householdId = Guid.NewGuid();
        _householdRepository.FindByIdAsync(householdId, Arg.Any<CancellationToken>())
            .Returns(NewHousehold(householdId, aiPlausibilityEnabled: true));
        SeedMainMeterWithABump(householdId);
        _aiPlausibilityClient.ClassifyAsync(
                Arg.Any<string>(), Arg.Any<IReadOnlyCollection<AiPlausibilityDirection>>(), Arg.Any<CancellationToken>())
            .Returns((AiPlausibilityDirection?)null);
        var sut = Sut();

        await sut.ExecuteAsync(householdId, Payload(householdId, SeedEvent(householdId).Id), TestContext.Current.CancellationToken);

        await _eventRepository.DidNotReceive().SetCorrelationAsync(
            Arg.Any<Guid>(), Arg.Any<string?>(), Arg.Any<DateTimeOffset?>(), Arg.Any<CancellationToken>());
    }

    // CorrelateEvent deliberately has no try/catch around the AI client call — it relies entirely
    // on IAiPlausibilityClient's own never-throw contract (NFR14). If that contract is ever
    // violated, the failure must surface unchanged rather than being silently swallowed here — it's
    // BackgroundJobProcessor's catch-all, one layer up, that turns it into a Failed job rather than
    // crashing the whole worker.
    [Fact]
    public async Task Propagates_unchanged_if_the_AI_client_violates_its_own_never_throw_contract()
    {
        var householdId = Guid.NewGuid();
        _householdRepository.FindByIdAsync(householdId, Arg.Any<CancellationToken>())
            .Returns(NewHousehold(householdId, aiPlausibilityEnabled: true));
        SeedMainMeterWithABump(householdId);
        _aiPlausibilityClient.ClassifyAsync(
                Arg.Any<string>(), Arg.Any<IReadOnlyCollection<AiPlausibilityDirection>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<AiPlausibilityDirection?>(new InvalidOperationException("contract violated")));
        var sut = Sut();

        await Should.ThrowAsync<InvalidOperationException>(() =>
            sut.ExecuteAsync(householdId, Payload(householdId, SeedEvent(householdId).Id), TestContext.Current.CancellationToken));
    }

    private async Task ExecuteWithABumpAsync(Guid householdId, Event @event)
    {
        _householdRepository.FindByIdAsync(householdId, Arg.Any<CancellationToken>())
            .Returns(NewHousehold(householdId, aiPlausibilityEnabled: true));
        SeedMainMeterWithABump(householdId);
        await Sut().ExecuteAsync(householdId, Payload(householdId, @event.Id), TestContext.Current.CancellationToken);
    }

    private void StubAi(AiPlausibilityDirection? answer) =>
        _aiPlausibilityClient.ClassifyAsync(
                Arg.Any<string>(), Arg.Any<IReadOnlyCollection<AiPlausibilityDirection>>(), Arg.Any<CancellationToken>())
            .Returns(answer);

    [Fact]
    public async Task Event_that_no_longer_exists_is_a_no_op()
    {
        var householdId = Guid.NewGuid();
        _householdRepository.FindByIdAsync(householdId, Arg.Any<CancellationToken>())
            .Returns(NewHousehold(householdId, aiPlausibilityEnabled: true));
        SeedMainMeterWithABump(householdId);
        var sut = Sut();

        await Should.NotThrowAsync(() =>
            sut.ExecuteAsync(householdId, Payload(householdId, Guid.NewGuid()), TestContext.Current.CancellationToken));

        await _aiPlausibilityClient.DidNotReceive().ClassifyAsync(
            Arg.Any<string>(), Arg.Any<IReadOnlyCollection<AiPlausibilityDirection>>(), Arg.Any<CancellationToken>());
        await _eventRepository.DidNotReceive().SetCorrelationAsync(
            Arg.Any<Guid>(), Arg.Any<string?>(), Arg.Any<DateTimeOffset?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Disabled_household_leaves_an_existing_correlation_untouched()
    {
        var householdId = Guid.NewGuid();
        var @event = SeedEvent(householdId, persistedDirection: "Bump");
        _householdRepository.FindByIdAsync(householdId, Arg.Any<CancellationToken>())
            .Returns(NewHousehold(householdId, aiPlausibilityEnabled: false));

        await Sut().ExecuteAsync(householdId, Payload(householdId, @event.Id), TestContext.Current.CancellationToken);

        await _eventRepository.DidNotReceive().SetCorrelationAsync(
            Arg.Any<Guid>(), Arg.Any<string?>(), Arg.Any<DateTimeOffset?>(), Arg.Any<CancellationToken>());
    }

    // AC #6: the AI-cost guard — an unchanged observed direction must neither call the backend nor write.
    [Fact]
    public async Task Observed_direction_equal_to_the_persisted_one_makes_no_AI_call_and_no_write()
    {
        var householdId = Guid.NewGuid();
        var @event = SeedEvent(householdId, persistedDirection: "Bump");
        StubAi(AiPlausibilityDirection.Bump);

        await ExecuteWithABumpAsync(householdId, @event);

        await _aiPlausibilityClient.DidNotReceive().ClassifyAsync(
            Arg.Any<string>(), Arg.Any<IReadOnlyCollection<AiPlausibilityDirection>>(), Arg.Any<CancellationToken>());
        await _eventRepository.DidNotReceive().SetCorrelationAsync(
            Arg.Any<Guid>(), Arg.Any<string?>(), Arg.Any<DateTimeOffset?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Observed_Bump_with_no_persisted_correlation_makes_one_AI_call_and_persists()
    {
        var householdId = Guid.NewGuid();
        var @event = SeedEvent(householdId);
        StubAi(AiPlausibilityDirection.Bump);

        await ExecuteWithABumpAsync(householdId, @event);

        await _aiPlausibilityClient.Received(1).ClassifyAsync(
            Arg.Any<string>(), Arg.Any<IReadOnlyCollection<AiPlausibilityDirection>>(), Arg.Any<CancellationToken>());
        await _eventRepository.Received(1).SetCorrelationAsync(
            @event.Id, "Bump", Arg.Is<DateTimeOffset?>(d => d.HasValue), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Observed_Bump_with_a_persisted_Dip_makes_one_AI_call_and_overwrites()
    {
        var householdId = Guid.NewGuid();
        var @event = SeedEvent(householdId, persistedDirection: "Dip");
        StubAi(AiPlausibilityDirection.Bump);

        await ExecuteWithABumpAsync(householdId, @event);

        await _aiPlausibilityClient.Received(1).ClassifyAsync(
            Arg.Any<string>(), Arg.Any<IReadOnlyCollection<AiPlausibilityDirection>>(), Arg.Any<CancellationToken>());
        await _eventRepository.Received(1).SetCorrelationAsync(
            @event.Id, "Bump", Arg.Is<DateTimeOffset?>(d => d.HasValue), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AI_returning_null_leaves_a_persisted_correlation_untouched()
    {
        // A null answer is indistinguishable from a transient AI failure (NFR14), so it must never
        // retract an already-persisted correlation.
        var householdId = Guid.NewGuid();
        var @event = SeedEvent(householdId, persistedDirection: "Dip");
        StubAi(null);

        await ExecuteWithABumpAsync(householdId, @event);

        await _eventRepository.DidNotReceive().SetCorrelationAsync(
            Arg.Any<Guid>(), Arg.Any<string?>(), Arg.Any<DateTimeOffset?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_persisted_direction_differing_only_in_case_still_counts_as_unchanged()
    {
        var householdId = Guid.NewGuid();
        var @event = SeedEvent(householdId, persistedDirection: "bump");

        await ExecuteWithABumpAsync(householdId, @event);

        await _aiPlausibilityClient.DidNotReceive().ClassifyAsync(
            Arg.Any<string>(), Arg.Any<IReadOnlyCollection<AiPlausibilityDirection>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task No_deviation_now_clears_a_persisted_correlation_without_an_AI_call()
    {
        var householdId = Guid.NewGuid();
        var @event = SeedEvent(householdId, persistedDirection: "Bump");
        _householdRepository.FindByIdAsync(householdId, Arg.Any<CancellationToken>())
            .Returns(NewHousehold(householdId, aiPlausibilityEnabled: true));
        var mainMeter = new MainMeter { Id = Guid.NewGuid(), HouseholdId = householdId, CreatedAtUtc = DateTimeOffset.UtcNow };
        _readingRepository.FindMainMeterByHouseholdAsync(householdId, Arg.Any<CancellationToken>()).Returns(mainMeter);
        _readingRepository.GetInWindowByMainMeterAsync(mainMeter.Id, Arg.Any<DateTimeOffset>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<MeterReading>)[Reading(mainMeter.Id, 1000m, OccurredAt)]);

        await Sut().ExecuteAsync(householdId, Payload(householdId, @event.Id), TestContext.Current.CancellationToken);

        await _aiPlausibilityClient.DidNotReceive().ClassifyAsync(
            Arg.Any<string>(), Arg.Any<IReadOnlyCollection<AiPlausibilityDirection>>(), Arg.Any<CancellationToken>());
        await _eventRepository.Received(1).SetCorrelationAsync(
            @event.Id, null, null, Arg.Any<CancellationToken>());
    }

    private (MeterReading Trigger, MeterRegressionPrompt Prompt) SeedOpenPrompt(Guid householdId, Guid mainMeterId, DateTimeOffset triggerTimestamp)
    {
        var trigger = Reading(mainMeterId, 5m, triggerTimestamp);
        var prompt = new MeterRegressionPrompt
        {
            Id = Guid.NewGuid(),
            HouseholdId = householdId,
            MainMeterId = mainMeterId,
            MeterReadingId = trigger.Id,
            PreviousMeterReadingId = Guid.NewGuid(),
            CreatedAtUtc = DateTimeOffset.UtcNow,
        };
        _regressionPromptRepository.GetOpenForHouseholdAsync(householdId, Arg.Any<CancellationToken>()).Returns(prompt);
        _readingRepository.FindByIdAsync(trigger.Id, Arg.Any<CancellationToken>()).Returns(trigger);
        return (trigger, prompt);
    }

    // AC #7: the trigger sits inside the window, so the bump-producing trailing reading (at/after
    // it) is excluded and only one reading remains — no deviation can be derived.
    [Fact]
    public async Task Open_prompt_inside_the_window_excludes_the_trigger_and_later_readings()
    {
        var householdId = Guid.NewGuid();
        var @event = SeedEvent(householdId);
        _householdRepository.FindByIdAsync(householdId, Arg.Any<CancellationToken>())
            .Returns(NewHousehold(householdId, aiPlausibilityEnabled: true));
        var mainMeter = SeedMainMeterWithABump(householdId);
        SeedOpenPrompt(householdId, mainMeter.Id, OccurredAt);
        StubAi(AiPlausibilityDirection.Bump);

        await Sut().ExecuteAsync(householdId, Payload(householdId, @event.Id), TestContext.Current.CancellationToken);

        await _aiPlausibilityClient.DidNotReceive().ClassifyAsync(
            Arg.Any<string>(), Arg.Any<IReadOnlyCollection<AiPlausibilityDirection>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Open_prompt_triggered_before_the_window_leaves_no_deviation_and_clears_a_stale_correlation()
    {
        var householdId = Guid.NewGuid();
        var @event = SeedEvent(householdId, persistedDirection: "Bump");
        _householdRepository.FindByIdAsync(householdId, Arg.Any<CancellationToken>())
            .Returns(NewHousehold(householdId, aiPlausibilityEnabled: true));
        var mainMeter = SeedMainMeterWithABump(householdId);
        SeedOpenPrompt(householdId, mainMeter.Id, OccurredAt - TimeSpan.FromDays(30));

        await Sut().ExecuteAsync(householdId, Payload(householdId, @event.Id), TestContext.Current.CancellationToken);

        await _aiPlausibilityClient.DidNotReceive().ClassifyAsync(
            Arg.Any<string>(), Arg.Any<IReadOnlyCollection<AiPlausibilityDirection>>(), Arg.Any<CancellationToken>());
        await _eventRepository.Received(1).SetCorrelationAsync(@event.Id, null, null, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Open_prompt_triggered_after_the_window_excludes_nothing()
    {
        var householdId = Guid.NewGuid();
        var @event = SeedEvent(householdId);
        _householdRepository.FindByIdAsync(householdId, Arg.Any<CancellationToken>())
            .Returns(NewHousehold(householdId, aiPlausibilityEnabled: true));
        var mainMeter = SeedMainMeterWithABump(householdId);
        SeedOpenPrompt(householdId, mainMeter.Id, OccurredAt + TimeSpan.FromDays(30));
        StubAi(AiPlausibilityDirection.Bump);

        await Sut().ExecuteAsync(householdId, Payload(householdId, @event.Id), TestContext.Current.CancellationToken);

        await _eventRepository.Received(1).SetCorrelationAsync(
            @event.Id, "Bump", Arg.Is<DateTimeOffset?>(d => d.HasValue), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Open_prompt_whose_trigger_reading_is_missing_logs_and_skips_without_throwing()
    {
        var householdId = Guid.NewGuid();
        var @event = SeedEvent(householdId, persistedDirection: "Bump");
        _householdRepository.FindByIdAsync(householdId, Arg.Any<CancellationToken>())
            .Returns(NewHousehold(householdId, aiPlausibilityEnabled: true));
        var mainMeter = SeedMainMeterWithABump(householdId);
        var (trigger, _) = SeedOpenPrompt(householdId, mainMeter.Id, OccurredAt);
        _readingRepository.FindByIdAsync(trigger.Id, Arg.Any<CancellationToken>()).Returns((MeterReading?)null);

        await Should.NotThrowAsync(() =>
            Sut().ExecuteAsync(householdId, Payload(householdId, @event.Id), TestContext.Current.CancellationToken));

        await _aiPlausibilityClient.DidNotReceive().ClassifyAsync(
            Arg.Any<string>(), Arg.Any<IReadOnlyCollection<AiPlausibilityDirection>>(), Arg.Any<CancellationToken>());
        await _eventRepository.DidNotReceive().SetCorrelationAsync(
            Arg.Any<Guid>(), Arg.Any<string?>(), Arg.Any<DateTimeOffset?>(), Arg.Any<CancellationToken>());
        _logger.ReceivedCalls().ShouldContain(c =>
            c.GetMethodInfo().Name == "Log" && (LogLevel)c.GetArguments()[0]! == LogLevel.Warning);
    }
}
