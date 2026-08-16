using EnergyTracker.Application.Ports;
using EnergyTracker.Domain;
using NSubstitute;
using Shouldly;

namespace EnergyTracker.Application.Tests;

public class ResolveMeterRegressionPromptTests
{
    private readonly IMeterRegressionPromptRepository _repository = Substitute.For<IMeterRegressionPromptRepository>();

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

    private ResolveMeterRegressionPrompt Sut() => new(_repository);

    [Fact]
    public async Task Reset_resolves_cleanly_with_no_capacity_involved()
    {
        var householdId = Guid.NewGuid();
        var mainMeterId = Guid.NewGuid();
        var prompt = NewOpenPrompt(householdId, mainMeterId);
        _repository.FindByIdAsync(householdId, prompt.Id, Arg.Any<CancellationToken>()).Returns(prompt);
        _repository.GetOpenForHouseholdAsync(householdId, Arg.Any<CancellationToken>()).Returns(prompt);
        _repository.ResolveAsync(Arg.Any<MeterRegressionPrompt>(), Arg.Any<CancellationToken>()).Returns(callInfo => callInfo.Arg<MeterRegressionPrompt>());
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
        _repository.ResolveAsync(Arg.Any<MeterRegressionPrompt>(), Arg.Any<CancellationToken>()).Returns(callInfo => callInfo.Arg<MeterRegressionPrompt>());
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
        _repository.ResolveAsync(Arg.Any<MeterRegressionPrompt>(), Arg.Any<CancellationToken>()).Returns(callInfo => callInfo.Arg<MeterRegressionPrompt>());
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
}
