using EnergyTracker.Application.Ports;
using EnergyTracker.Domain;
using NSubstitute;
using Shouldly;

namespace EnergyTracker.Application.Tests;

public class CorrelateEventTests
{
    private readonly IHouseholdRepository _householdRepository = Substitute.For<IHouseholdRepository>();
    private readonly IMeterReadingRepository _readingRepository = Substitute.For<IMeterReadingRepository>();
    private readonly IEventRepository _eventRepository = Substitute.For<IEventRepository>();
    private readonly IAiPlausibilityClient _aiPlausibilityClient = Substitute.For<IAiPlausibilityClient>();

    private CorrelateEvent Sut() => new(_householdRepository, _readingRepository, _eventRepository, _aiPlausibilityClient);

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
            Arg.Any<Guid>(), Arg.Any<string?>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>());
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
            Arg.Any<Guid>(), Arg.Any<string?>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>());
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
            Arg.Any<Guid>(), Arg.Any<string?>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>());
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

        await sut.ExecuteAsync(householdId, Payload(householdId, Guid.NewGuid()), TestContext.Current.CancellationToken);

        await _aiPlausibilityClient.DidNotReceive().ClassifyAsync(
            Arg.Any<string>(), Arg.Any<IReadOnlyCollection<AiPlausibilityDirection>>(), Arg.Any<CancellationToken>());
        await _eventRepository.DidNotReceive().SetCorrelationAsync(
            Arg.Any<Guid>(), Arg.Any<string?>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Enabled_with_a_deviation_and_an_AI_match_persists_the_correlation()
    {
        var householdId = Guid.NewGuid();
        var eventId = Guid.NewGuid();
        _householdRepository.FindByIdAsync(householdId, Arg.Any<CancellationToken>())
            .Returns(NewHousehold(householdId, aiPlausibilityEnabled: true));
        SeedMainMeterWithABump(householdId);
        _aiPlausibilityClient.ClassifyAsync(
                "gaming session 3h", Arg.Is<IReadOnlyCollection<AiPlausibilityDirection>>(d => d.Contains(AiPlausibilityDirection.Bump)),
                Arg.Any<CancellationToken>())
            .Returns(AiPlausibilityDirection.Bump);
        var sut = Sut();

        await sut.ExecuteAsync(householdId, Payload(householdId, eventId), TestContext.Current.CancellationToken);

        await _eventRepository.Received(1).SetCorrelationAsync(eventId, "Bump", Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>());
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

        await sut.ExecuteAsync(householdId, Payload(householdId, Guid.NewGuid()), TestContext.Current.CancellationToken);

        await _eventRepository.DidNotReceive().SetCorrelationAsync(
            Arg.Any<Guid>(), Arg.Any<string?>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>());
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
            sut.ExecuteAsync(householdId, Payload(householdId, Guid.NewGuid()), TestContext.Current.CancellationToken));
    }
}
