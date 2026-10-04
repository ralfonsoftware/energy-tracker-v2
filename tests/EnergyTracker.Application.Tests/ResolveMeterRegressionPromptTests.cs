using EnergyTracker.Application.Ports;
using EnergyTracker.Domain;
using NSubstitute;
using Shouldly;

namespace EnergyTracker.Application.Tests;

public class ResolveMeterRegressionPromptTests
{
    private readonly IMeterRegressionPromptRepository _repository = Substitute.For<IMeterRegressionPromptRepository>();
    private readonly IMeterReadingRepository _readingRepository = Substitute.For<IMeterReadingRepository>();
    private readonly IHouseholdRepository _householdRepository = Substitute.For<IHouseholdRepository>();
    private readonly IEventRepository _eventRepository = Substitute.For<IEventRepository>();
    private readonly IBackgroundJobQueue _jobQueue = Substitute.For<IBackgroundJobQueue>();

    private RequeueEventCorrelations Requeue() => new(
        _householdRepository, _eventRepository, _readingRepository, _jobQueue,
        Substitute.For<Microsoft.Extensions.Logging.ILogger<RequeueEventCorrelations>>());

    private static MeterRegressionPrompt NewOpenPrompt(Guid householdId, Guid mainMeterId) => new()
    {
        Id = Guid.NewGuid(),
        HouseholdId = householdId,
        MainMeterId = mainMeterId,
        MeterReadingId = Guid.NewGuid(),
        PreviousMeterReadingId = Guid.NewGuid(),
        CreatedAtUtc = DateTimeOffset.UtcNow,
        Classification = null,
        ResolvedAtUtc = null,
    };

    private ResolveMeterRegressionPrompt Sut() => new(_repository, Requeue());

    [Fact]
    public async Task Reset_resolves_cleanly_with_no_capacity_involved()
    {
        var householdId = Guid.NewGuid();
        var mainMeterId = Guid.NewGuid();
        var prompt = NewOpenPrompt(householdId, mainMeterId);
        _repository.FindByIdAsync(householdId, prompt.Id, Arg.Any<CancellationToken>()).Returns(prompt);
        _repository.GetOpenForHouseholdAsync(householdId, Arg.Any<CancellationToken>()).Returns(prompt);
        _repository.ResolveAsync(Arg.Any<MeterRegressionPrompt>(), Arg.Any<CancellationToken>()).Returns(true);
        var sut = Sut();

        var result = await sut.ExecuteAsync(householdId, prompt.Id, MeterRegressionClassification.Reset, null, TestContext.Current.CancellationToken);

        result.Classification.ShouldBe(MeterRegressionClassification.Reset);
        result.DigitCapacityKwh.ShouldBeNull();
        result.ResolvedAtUtc.ShouldNotBeNull();
        await _repository.DidNotReceive().GetMainMeterDigitCapacityAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
        await _repository.DidNotReceive().SetMainMeterDigitCapacityIfUnsetAsync(Arg.Any<Guid>(), Arg.Any<decimal>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Rollover_with_an_explicit_digit_capacity_persists_it_and_updates_MainMeter()
    {
        var householdId = Guid.NewGuid();
        var mainMeterId = Guid.NewGuid();
        var prompt = NewOpenPrompt(householdId, mainMeterId);
        _repository.FindByIdAsync(householdId, prompt.Id, Arg.Any<CancellationToken>()).Returns(prompt);
        _repository.GetOpenForHouseholdAsync(householdId, Arg.Any<CancellationToken>()).Returns(prompt);
        _repository.ResolveAsync(Arg.Any<MeterRegressionPrompt>(), Arg.Any<CancellationToken>()).Returns(true);
        var sut = Sut();

        var result = await sut.ExecuteAsync(householdId, prompt.Id, MeterRegressionClassification.Rollover, 99999m, TestContext.Current.CancellationToken);

        result.Classification.ShouldBe(MeterRegressionClassification.Rollover);
        result.DigitCapacityKwh.ShouldBe(99999m);
        await _repository.Received(1).SetMainMeterDigitCapacityIfUnsetAsync(mainMeterId, 99999m, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Rollover_with_no_explicit_capacity_but_an_existing_MainMeter_capacity_succeeds_using_the_stored_value()
    {
        var householdId = Guid.NewGuid();
        var mainMeterId = Guid.NewGuid();
        var prompt = NewOpenPrompt(householdId, mainMeterId);
        _repository.FindByIdAsync(householdId, prompt.Id, Arg.Any<CancellationToken>()).Returns(prompt);
        _repository.GetOpenForHouseholdAsync(householdId, Arg.Any<CancellationToken>()).Returns(prompt);
        _repository.GetMainMeterDigitCapacityAsync(mainMeterId, Arg.Any<CancellationToken>()).Returns(88888m);
        _repository.ResolveAsync(Arg.Any<MeterRegressionPrompt>(), Arg.Any<CancellationToken>()).Returns(true);
        var sut = Sut();

        var result = await sut.ExecuteAsync(householdId, prompt.Id, MeterRegressionClassification.Rollover, null, TestContext.Current.CancellationToken);

        result.DigitCapacityKwh.ShouldBe(88888m);
        await _repository.Received(1).SetMainMeterDigitCapacityIfUnsetAsync(mainMeterId, 88888m, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Rollover_with_neither_an_explicit_nor_stored_capacity_throws()
    {
        var householdId = Guid.NewGuid();
        var mainMeterId = Guid.NewGuid();
        var prompt = NewOpenPrompt(householdId, mainMeterId);
        _repository.FindByIdAsync(householdId, prompt.Id, Arg.Any<CancellationToken>()).Returns(prompt);
        _repository.GetOpenForHouseholdAsync(householdId, Arg.Any<CancellationToken>()).Returns(prompt);
        _repository.GetMainMeterDigitCapacityAsync(mainMeterId, Arg.Any<CancellationToken>()).Returns((decimal?)null);
        var sut = Sut();

        await Should.ThrowAsync<MeterRegressionValidationException>(() =>
            sut.ExecuteAsync(householdId, prompt.Id, MeterRegressionClassification.Rollover, null, TestContext.Current.CancellationToken));

        await _repository.DidNotReceive().ResolveAsync(Arg.Any<MeterRegressionPrompt>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public async Task Rollover_with_a_non_positive_digit_capacity_throws(decimal digitCapacityKwh)
    {
        var householdId = Guid.NewGuid();
        var mainMeterId = Guid.NewGuid();
        var prompt = NewOpenPrompt(householdId, mainMeterId);
        _repository.FindByIdAsync(householdId, prompt.Id, Arg.Any<CancellationToken>()).Returns(prompt);
        _repository.GetOpenForHouseholdAsync(householdId, Arg.Any<CancellationToken>()).Returns(prompt);
        var sut = Sut();

        await Should.ThrowAsync<MeterRegressionValidationException>(() =>
            sut.ExecuteAsync(householdId, prompt.Id, MeterRegressionClassification.Rollover, digitCapacityKwh, TestContext.Current.CancellationToken));

        await _repository.DidNotReceive().ResolveAsync(Arg.Any<MeterRegressionPrompt>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Resolving_an_already_resolved_prompt_throws()
    {
        var householdId = Guid.NewGuid();
        var mainMeterId = Guid.NewGuid();
        var prompt = NewOpenPrompt(householdId, mainMeterId);
        prompt.Classification = MeterRegressionClassification.Reset;
        prompt.ResolvedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-5);
        _repository.FindByIdAsync(householdId, prompt.Id, Arg.Any<CancellationToken>()).Returns(prompt);
        var sut = Sut();

        await Should.ThrowAsync<MeterRegressionPromptNotOpenException>(() =>
            sut.ExecuteAsync(householdId, prompt.Id, MeterRegressionClassification.Reset, null, TestContext.Current.CancellationToken));

        await _repository.DidNotReceive().ResolveAsync(Arg.Any<MeterRegressionPrompt>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Resolving_a_prompt_that_is_not_the_current_open_one_throws()
    {
        var householdId = Guid.NewGuid();
        var mainMeterId = Guid.NewGuid();
        var queuedPrompt = NewOpenPrompt(householdId, mainMeterId);
        var earlierOpenPrompt = NewOpenPrompt(householdId, mainMeterId);
        _repository.FindByIdAsync(householdId, queuedPrompt.Id, Arg.Any<CancellationToken>()).Returns(queuedPrompt);
        _repository.GetOpenForHouseholdAsync(householdId, Arg.Any<CancellationToken>()).Returns(earlierOpenPrompt);
        var sut = Sut();

        await Should.ThrowAsync<MeterRegressionPromptNotOpenException>(() =>
            sut.ExecuteAsync(householdId, queuedPrompt.Id, MeterRegressionClassification.Reset, null, TestContext.Current.CancellationToken));

        await _repository.DidNotReceive().ResolveAsync(Arg.Any<MeterRegressionPrompt>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_nonexistent_prompt_id_throws_not_found()
    {
        var householdId = Guid.NewGuid();
        var promptId = Guid.NewGuid();
        _repository.FindByIdAsync(householdId, promptId, Arg.Any<CancellationToken>()).Returns((MeterRegressionPrompt?)null);
        var sut = Sut();

        await Should.ThrowAsync<MeterRegressionPromptNotFoundException>(() =>
            sut.ExecuteAsync(householdId, promptId, MeterRegressionClassification.Reset, null, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Losing_the_resolve_race_to_a_concurrent_request_throws_not_open()
    {
        var householdId = Guid.NewGuid();
        var mainMeterId = Guid.NewGuid();
        var prompt = NewOpenPrompt(householdId, mainMeterId);
        _repository.FindByIdAsync(householdId, prompt.Id, Arg.Any<CancellationToken>()).Returns(prompt);
        _repository.GetOpenForHouseholdAsync(householdId, Arg.Any<CancellationToken>()).Returns(prompt);
        // A concurrent request resolved this exact prompt between our reads above and our write —
        // the conditional UPDATE affects zero rows.
        _repository.ResolveAsync(Arg.Any<MeterRegressionPrompt>(), Arg.Any<CancellationToken>()).Returns(false);
        var sut = Sut();

        await Should.ThrowAsync<MeterRegressionPromptNotOpenException>(() =>
            sut.ExecuteAsync(householdId, prompt.Id, MeterRegressionClassification.Reset, null, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Rollover_with_a_digit_capacity_at_or_above_the_max_throws()
    {
        var householdId = Guid.NewGuid();
        var mainMeterId = Guid.NewGuid();
        var prompt = NewOpenPrompt(householdId, mainMeterId);
        _repository.FindByIdAsync(householdId, prompt.Id, Arg.Any<CancellationToken>()).Returns(prompt);
        _repository.GetOpenForHouseholdAsync(householdId, Arg.Any<CancellationToken>()).Returns(prompt);
        var sut = Sut();

        await Should.ThrowAsync<MeterRegressionValidationException>(() =>
            sut.ExecuteAsync(householdId, prompt.Id, MeterRegressionClassification.Rollover, 1_000_000_000_000_000m, TestContext.Current.CancellationToken));

        await _repository.DidNotReceive().ResolveAsync(Arg.Any<MeterRegressionPrompt>(), Arg.Any<CancellationToken>());
    }

    // Story 10.2 (AC #8)
    [Fact]
    public async Task A_successful_resolve_requeues_Events_from_the_triggering_reading_minus_7_days()
    {
        var householdId = Guid.NewGuid();
        var prompt = NewOpenPrompt(householdId, Guid.NewGuid());
        var trigger = new MeterReading
        {
            Id = prompt.MeterReadingId, HouseholdId = householdId, MainMeterId = prompt.MainMeterId, KwhValue = 5m,
            ReadingTimestamp = new DateTimeOffset(2026, 5, 20, 8, 0, 0, TimeSpan.Zero),
            IdempotencyKey = Guid.NewGuid(), CreatedAtUtc = DateTimeOffset.UtcNow,
        };
        _repository.FindByIdAsync(householdId, prompt.Id, Arg.Any<CancellationToken>()).Returns(prompt);
        _repository.GetOpenForHouseholdAsync(householdId, Arg.Any<CancellationToken>()).Returns(prompt);
        _repository.ResolveAsync(Arg.Any<MeterRegressionPrompt>(), Arg.Any<CancellationToken>()).Returns(true);
        _readingRepository.FindByIdAsync(trigger.Id, Arg.Any<CancellationToken>()).Returns(trigger);
        _householdRepository.FindByIdAsync(householdId, Arg.Any<CancellationToken>()).Returns(new Household
        {
            Id = householdId, Locale = "en-US", Currency = "USD", CreatedAtUtc = DateTimeOffset.UtcNow, AiPlausibilityEnabled = true,
        });

        await Sut().ExecuteAsync(householdId, prompt.Id, MeterRegressionClassification.Reset, null, TestContext.Current.CancellationToken);

        await _eventRepository.Received(1).GetByOccurredAtRangeAsync(
            trigger.ReadingTimestamp.AddDays(-7),
            Arg.Is<DateTimeOffset>(to => to > DateTimeOffset.UtcNow && to <= DateTimeOffset.UtcNow.AddMinutes(10)),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_lost_race_resolve_does_not_requeue()
    {
        var householdId = Guid.NewGuid();
        var prompt = NewOpenPrompt(householdId, Guid.NewGuid());
        _repository.FindByIdAsync(householdId, prompt.Id, Arg.Any<CancellationToken>()).Returns(prompt);
        _repository.GetOpenForHouseholdAsync(householdId, Arg.Any<CancellationToken>()).Returns(prompt);
        _repository.ResolveAsync(Arg.Any<MeterRegressionPrompt>(), Arg.Any<CancellationToken>()).Returns(false);

        await Should.ThrowAsync<MeterRegressionPromptNotOpenException>(() =>
            Sut().ExecuteAsync(householdId, prompt.Id, MeterRegressionClassification.Reset, null, TestContext.Current.CancellationToken));

        await _eventRepository.DidNotReceiveWithAnyArgs().GetByOccurredAtRangeAsync(default, default, default);
    }

    [Fact]
    public async Task A_failing_or_missing_trigger_lookup_never_fails_the_committed_resolve()
    {
        var householdId = Guid.NewGuid();

        // A resolve mutates the prompt, so each scenario needs its own open prompt.
        async Task ResolveWithTriggerLookupAsync(Func<Task<MeterReading?>> lookup)
        {
            var prompt = NewOpenPrompt(householdId, Guid.NewGuid());
            _repository.FindByIdAsync(householdId, prompt.Id, Arg.Any<CancellationToken>()).Returns(prompt);
            _repository.GetOpenForHouseholdAsync(householdId, Arg.Any<CancellationToken>()).Returns(prompt);
            _repository.ResolveAsync(Arg.Any<MeterRegressionPrompt>(), Arg.Any<CancellationToken>()).Returns(true);
            _readingRepository.FindByIdAsync(prompt.MeterReadingId, Arg.Any<CancellationToken>()).Returns(_ => lookup());

            await Should.NotThrowAsync(() => Sut().ExecuteAsync(
                householdId, prompt.Id, MeterRegressionClassification.Reset, null, TestContext.Current.CancellationToken));
        }

        await ResolveWithTriggerLookupAsync(() => Task.FromException<MeterReading?>(new InvalidOperationException("db blip")));
        await ResolveWithTriggerLookupAsync(() => Task.FromResult<MeterReading?>(null));

        await _eventRepository.DidNotReceiveWithAnyArgs().GetByOccurredAtRangeAsync(default, default, default);
    }
}
