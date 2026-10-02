using EnergyTracker.Application.Ports;
using EnergyTracker.Domain;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Shouldly;

namespace EnergyTracker.Application.Tests;

public class RequeueEventCorrelationsTests
{
    private readonly IHouseholdRepository _householdRepository = Substitute.For<IHouseholdRepository>();
    private readonly IEventRepository _eventRepository = Substitute.For<IEventRepository>();
    private readonly IMeterReadingRepository _readingRepository = Substitute.For<IMeterReadingRepository>();
    private readonly IBackgroundJobQueue _jobQueue = Substitute.For<IBackgroundJobQueue>();
    private readonly ILogger<RequeueEventCorrelations> _logger = Substitute.For<ILogger<RequeueEventCorrelations>>();

    private static readonly DateTimeOffset From = new(2026, 6, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset To = From.AddDays(14);

    private RequeueEventCorrelations Sut() => new(_householdRepository, _eventRepository, _readingRepository, _jobQueue, _logger);

    // ILogger.Log<TState> is generic over an internal state type, so match the recorded calls by level and exception.
    private bool LoggedWarning(string exceptionMessage) => _logger.ReceivedCalls().Any(c =>
        c.GetMethodInfo().Name == "Log"
        && (LogLevel)c.GetArguments()[0]! == LogLevel.Warning
        && c.GetArguments()[3] is Exception ex && ex.Message == exceptionMessage);

    private void SeedHousehold(Guid householdId, bool aiPlausibilityEnabled) =>
        _householdRepository.FindByIdAsync(householdId, Arg.Any<CancellationToken>()).Returns(new Household
        {
            Id = householdId,
            Locale = "en-US",
            Currency = "USD",
            CreatedAtUtc = DateTimeOffset.UtcNow,
            AiPlausibilityEnabled = aiPlausibilityEnabled,
        });

    private static Event NewEvent(Guid householdId, string description) => new()
    {
        Id = Guid.NewGuid(),
        HouseholdId = householdId,
        Description = description,
        OccurredAt = From.AddDays(3),
        CreatedAtUtc = DateTimeOffset.UtcNow,
    };

    private void SeedEvents(params Event[] events) =>
        _eventRepository.GetByOccurredAtRangeAsync(From, To, Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<Event>)events);

    [Fact]
    public async Task Enqueues_one_CorrelateEvent_job_per_Event_in_range()
    {
        var householdId = Guid.NewGuid();
        SeedHousehold(householdId, aiPlausibilityEnabled: true);
        var first = NewEvent(householdId, "cooked 2h");
        var second = NewEvent(householdId, "gaming 3h");
        SeedEvents(first, second);

        await Sut().ExecuteAsync(householdId, From, To, TestContext.Current.CancellationToken);

        await _jobQueue.Received(1).EnqueueAsync(
            Arg.Is<JobEnvelope<CorrelateEventPayload>>(e =>
                e.HouseholdId == householdId
                && e.JobType == JobTypes.CorrelateEvent
                && e.Payload.EventId == first.Id
                && e.Payload.HouseholdId == householdId
                && e.Payload.OccurredAt == first.OccurredAt
                && e.Payload.Description == "cooked 2h"),
            Arg.Any<CancellationToken>());
        await _jobQueue.Received(1).EnqueueAsync(
            Arg.Is<JobEnvelope<CorrelateEventPayload>>(e => e.Payload.EventId == second.Id),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task No_Events_in_range_enqueues_nothing()
    {
        var householdId = Guid.NewGuid();
        SeedHousehold(householdId, aiPlausibilityEnabled: true);
        SeedEvents();

        await Sut().ExecuteAsync(householdId, From, To, TestContext.Current.CancellationToken);

        await _jobQueue.DidNotReceive().EnqueueAsync(Arg.Any<JobEnvelope<CorrelateEventPayload>>(), Arg.Any<CancellationToken>());
    }

    // Ask First decision 3 (Ralf, 2026-10-02): the trigger carries a documented AD-8 exception and
    // skips AI-off Households, so no cheap-but-pointless job rows accumulate for them.
    [Fact]
    public async Task AI_disabled_household_enqueues_nothing_and_does_not_query_Events()
    {
        var householdId = Guid.NewGuid();
        SeedHousehold(householdId, aiPlausibilityEnabled: false);
        SeedEvents(NewEvent(householdId, "cooked 2h"));

        await Sut().ExecuteAsync(householdId, From, To, TestContext.Current.CancellationToken);

        await _eventRepository.DidNotReceive().GetByOccurredAtRangeAsync(
            Arg.Any<DateTimeOffset>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>());
        await _jobQueue.DidNotReceive().EnqueueAsync(Arg.Any<JobEnvelope<CorrelateEventPayload>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Missing_household_enqueues_nothing()
    {
        _householdRepository.FindByIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns((Household?)null);

        await Sut().ExecuteAsync(Guid.NewGuid(), From, To, TestContext.Current.CancellationToken);

        await _jobQueue.DidNotReceive().EnqueueAsync(Arg.Any<JobEnvelope<CorrelateEventPayload>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task An_enqueue_failure_for_one_Event_is_swallowed_and_the_others_are_still_attempted()
    {
        var householdId = Guid.NewGuid();
        SeedHousehold(householdId, aiPlausibilityEnabled: true);
        var first = NewEvent(householdId, "first");
        var second = NewEvent(householdId, "second");
        SeedEvents(first, second);
        _jobQueue.EnqueueAsync(
                Arg.Is<JobEnvelope<CorrelateEventPayload>>(e => e.Payload.EventId == first.Id), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new InvalidOperationException("queue down")));

        await Should.NotThrowAsync(() => Sut().ExecuteAsync(householdId, From, To, TestContext.Current.CancellationToken));

        await _jobQueue.Received(1).EnqueueAsync(
            Arg.Is<JobEnvelope<CorrelateEventPayload>>(e => e.Payload.EventId == second.Id), Arg.Any<CancellationToken>());
        LoggedWarning("queue down").ShouldBeTrue();
    }

    [Fact]
    public async Task A_repository_failure_is_swallowed_and_logged()
    {
        var householdId = Guid.NewGuid();
        SeedHousehold(householdId, aiPlausibilityEnabled: true);
        _eventRepository.GetByOccurredAtRangeAsync(Arg.Any<DateTimeOffset>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<IReadOnlyList<Event>>(new InvalidOperationException("db blip")));

        await Should.NotThrowAsync(() => Sut().ExecuteAsync(householdId, From, To, TestContext.Current.CancellationToken));

        LoggedWarning("db blip").ShouldBeTrue();
    }

    [Fact]
    public async Task Cancellation_is_not_swallowed()
    {
        var householdId = Guid.NewGuid();
        _householdRepository.FindByIdAsync(householdId, Arg.Any<CancellationToken>())
            .Returns(Task.FromException<Household?>(new OperationCanceledException()));

        await Should.ThrowAsync<OperationCanceledException>(() =>
            Sut().ExecuteAsync(householdId, From, To, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Cancellation_during_the_Event_query_is_not_swallowed()
    {
        var householdId = Guid.NewGuid();
        SeedHousehold(householdId, aiPlausibilityEnabled: true);
        _eventRepository.GetByOccurredAtRangeAsync(Arg.Any<DateTimeOffset>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<IReadOnlyList<Event>>(new OperationCanceledException()));

        await Should.ThrowAsync<OperationCanceledException>(() =>
            Sut().ExecuteAsync(householdId, From, To, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Cancellation_during_the_enqueue_is_not_swallowed()
    {
        var householdId = Guid.NewGuid();
        SeedHousehold(householdId, aiPlausibilityEnabled: true);
        SeedEvents(NewEvent(householdId, "cooked 2h"));
        _jobQueue.EnqueueAsync(Arg.Any<JobEnvelope<CorrelateEventPayload>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new OperationCanceledException()));

        await Should.ThrowAsync<OperationCanceledException>(() =>
            Sut().ExecuteAsync(householdId, From, To, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ExecuteFromReadingAsync_requeues_from_the_trigger_minus_7_days_to_now_plus_skew()
    {
        var householdId = Guid.NewGuid();
        SeedHousehold(householdId, aiPlausibilityEnabled: true);
        var trigger = new MeterReading
        {
            Id = Guid.NewGuid(), HouseholdId = householdId, MainMeterId = Guid.NewGuid(), KwhValue = 5m,
            ReadingTimestamp = From, IdempotencyKey = Guid.NewGuid(), CreatedAtUtc = DateTimeOffset.UtcNow,
        };
        _readingRepository.FindByIdAsync(trigger.Id, Arg.Any<CancellationToken>()).Returns(trigger);

        await Sut().ExecuteFromReadingAsync(householdId, trigger.Id, TestContext.Current.CancellationToken);

        await _eventRepository.Received(1).GetByOccurredAtRangeAsync(
            From.AddDays(-7),
            Arg.Is<DateTimeOffset>(to => to > DateTimeOffset.UtcNow && to <= DateTimeOffset.UtcNow.AddMinutes(10)),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteFromReadingAsync_with_a_missing_trigger_logs_and_requeues_nothing()
    {
        var householdId = Guid.NewGuid();
        _readingRepository.FindByIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns((MeterReading?)null);

        await Should.NotThrowAsync(() =>
            Sut().ExecuteFromReadingAsync(householdId, Guid.NewGuid(), TestContext.Current.CancellationToken));

        await _eventRepository.DidNotReceiveWithAnyArgs().GetByOccurredAtRangeAsync(default, default, default);
        _logger.ReceivedCalls().ShouldContain(c =>
            c.GetMethodInfo().Name == "Log" && (LogLevel)c.GetArguments()[0]! == LogLevel.Warning);
    }

    [Fact]
    public async Task ExecuteFromReadingAsync_swallows_a_trigger_lookup_failure_but_not_cancellation()
    {
        var householdId = Guid.NewGuid();
        _readingRepository.FindByIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<MeterReading?>(new InvalidOperationException("db blip")));

        await Should.NotThrowAsync(() =>
            Sut().ExecuteFromReadingAsync(householdId, Guid.NewGuid(), TestContext.Current.CancellationToken));
        LoggedWarning("db blip").ShouldBeTrue();

        _readingRepository.FindByIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<MeterReading?>(new OperationCanceledException()));
        await Should.ThrowAsync<OperationCanceledException>(() =>
            Sut().ExecuteFromReadingAsync(householdId, Guid.NewGuid(), TestContext.Current.CancellationToken));
    }
}
