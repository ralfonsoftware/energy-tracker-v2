using EnergyTracker.Application.Ports;
using EnergyTracker.Domain;
using NSubstitute;
using Shouldly;

namespace EnergyTracker.Application.Tests;

public class CreateMeterReadingTests
{
    private readonly IMeterReadingRepository _repository = Substitute.For<IMeterReadingRepository>();
    private readonly IMeterRegressionPromptRepository _regressionPromptRepository = Substitute.For<IMeterRegressionPromptRepository>();

    private static MainMeter NewMainMeter(Guid householdId) => new()
    {
        Id = Guid.NewGuid(),
        HouseholdId = householdId,
        CreatedAtUtc = DateTimeOffset.UtcNow,
    };

    private CreateMeterReading Sut() => new(_repository, _regressionPromptRepository);

    public CreateMeterReadingTests()
    {
        // Default: no preceding reading exists, so regression detection is a no-op unless a test
        // explicitly arranges FindImmediatelyPrecedingAsync to return one.
        _repository.FindImmediatelyPrecedingAsync(Arg.Any<Guid>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns((MeterReading?)null);
    }

    [Fact]
    public async Task Creates_and_persists_a_Meter_Reading_for_the_callers_own_Household()
    {
        var householdId = Guid.NewGuid();
        var mainMeter = NewMainMeter(householdId);
        _repository.FindByIdempotencyKeyAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns((MeterReading?)null);
        _repository.GetOrCreateMainMeterAsync(householdId, Arg.Any<CancellationToken>()).Returns(mainMeter);
        _repository.AddAsync(Arg.Any<MeterReading>(), Arg.Any<CancellationToken>()).Returns(callInfo => callInfo.Arg<MeterReading>());
        var sut = Sut();
        var readingTimestamp = DateTimeOffset.UtcNow;
        var idempotencyKey = Guid.NewGuid();

        var result = await sut.ExecuteAsync(householdId, 4821.5m, readingTimestamp, idempotencyKey, TestContext.Current.CancellationToken);

        result.HouseholdId.ShouldBe(householdId);
        result.MainMeterId.ShouldBe(mainMeter.Id);
        result.KwhValue.ShouldBe(4821.5m);
        result.ReadingTimestamp.ShouldBe(readingTimestamp);
        result.IdempotencyKey.ShouldBe(idempotencyKey);
        await _repository.Received(1).AddAsync(Arg.Is<MeterReading>(r => r.IdempotencyKey == idempotencyKey), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-3500.5)]
    public async Task Rejects_a_kWh_value_that_is_not_positive(decimal kwhValue)
    {
        var sut = Sut();

        await Should.ThrowAsync<MeterReadingValidationException>(() =>
            sut.ExecuteAsync(Guid.NewGuid(), kwhValue, DateTimeOffset.UtcNow, Guid.NewGuid(), TestContext.Current.CancellationToken));

        await _repository.DidNotReceive().AddAsync(Arg.Any<MeterReading>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Rejects_a_kWh_value_that_would_overflow_the_decimal_18_2_column()
    {
        var sut = Sut();

        await Should.ThrowAsync<MeterReadingValidationException>(() =>
            sut.ExecuteAsync(Guid.NewGuid(), 1_000_000_000_000_000m, DateTimeOffset.UtcNow, Guid.NewGuid(), TestContext.Current.CancellationToken));

        await _repository.DidNotReceive().AddAsync(Arg.Any<MeterReading>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task An_idempotency_key_replay_returns_the_existing_reading_without_a_second_AddAsync_call()
    {
        var householdId = Guid.NewGuid();
        var idempotencyKey = Guid.NewGuid();
        var existing = new MeterReading
        {
            Id = Guid.NewGuid(),
            HouseholdId = householdId,
            MainMeterId = Guid.NewGuid(),
            KwhValue = 4821.5m,
            ReadingTimestamp = DateTimeOffset.UtcNow,
            IdempotencyKey = idempotencyKey,
            CreatedAtUtc = DateTimeOffset.UtcNow,
        };
        _repository.FindByIdempotencyKeyAsync(idempotencyKey, Arg.Any<CancellationToken>()).Returns(existing);
        var sut = Sut();

        var result = await sut.ExecuteAsync(householdId, 4821.5m, DateTimeOffset.UtcNow, idempotencyKey, TestContext.Current.CancellationToken);

        result.ShouldBe(existing);
        await _repository.DidNotReceive().GetOrCreateMainMeterAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
        await _repository.DidNotReceive().AddAsync(Arg.Any<MeterReading>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Two_distinct_idempotency_keys_both_persist_even_at_an_identical_ReadingTimestamp()
    {
        var householdId = Guid.NewGuid();
        var mainMeter = NewMainMeter(householdId);
        var readingTimestamp = DateTimeOffset.UtcNow;
        _repository.FindByIdempotencyKeyAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns((MeterReading?)null);
        _repository.GetOrCreateMainMeterAsync(householdId, Arg.Any<CancellationToken>()).Returns(mainMeter);
        _repository.AddAsync(Arg.Any<MeterReading>(), Arg.Any<CancellationToken>()).Returns(callInfo => callInfo.Arg<MeterReading>());
        var sut = Sut();

        await sut.ExecuteAsync(householdId, 100m, readingTimestamp, Guid.NewGuid(), TestContext.Current.CancellationToken);
        await sut.ExecuteAsync(householdId, 105m, readingTimestamp, Guid.NewGuid(), TestContext.Current.CancellationToken);

        await _repository.Received(2).AddAsync(Arg.Any<MeterReading>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetOrCreateMainMeterAsync_is_called_because_a_Main_Meter_need_not_pre_exist()
    {
        var householdId = Guid.NewGuid();
        var mainMeter = NewMainMeter(householdId);
        _repository.FindByIdempotencyKeyAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns((MeterReading?)null);
        _repository.GetOrCreateMainMeterAsync(householdId, Arg.Any<CancellationToken>()).Returns(mainMeter);
        _repository.AddAsync(Arg.Any<MeterReading>(), Arg.Any<CancellationToken>()).Returns(callInfo => callInfo.Arg<MeterReading>());
        var sut = Sut();

        await sut.ExecuteAsync(householdId, 100m, DateTimeOffset.UtcNow, Guid.NewGuid(), TestContext.Current.CancellationToken);

        await _repository.Received(1).GetOrCreateMainMeterAsync(householdId, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_reading_lower_than_the_immediately_preceding_one_raises_a_regression_prompt()
    {
        var householdId = Guid.NewGuid();
        var mainMeter = NewMainMeter(householdId);
        var preceding = new MeterReading
        {
            Id = Guid.NewGuid(),
            HouseholdId = householdId,
            MainMeterId = mainMeter.Id,
            KwhValue = 14302m,
            ReadingTimestamp = DateTimeOffset.UtcNow.AddDays(-1),
            IdempotencyKey = Guid.NewGuid(),
            CreatedAtUtc = DateTimeOffset.UtcNow.AddDays(-1),
        };
        _repository.FindByIdempotencyKeyAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns((MeterReading?)null);
        _repository.GetOrCreateMainMeterAsync(householdId, Arg.Any<CancellationToken>()).Returns(mainMeter);
        _repository.AddAsync(Arg.Any<MeterReading>(), Arg.Any<CancellationToken>()).Returns(callInfo => callInfo.Arg<MeterReading>());
        _repository.FindImmediatelyPrecedingAsync(mainMeter.Id, Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(preceding);
        var sut = Sut();

        var result = await sut.ExecuteAsync(householdId, 412m, DateTimeOffset.UtcNow, Guid.NewGuid(), TestContext.Current.CancellationToken);

        await _regressionPromptRepository.Received(1).AddAsync(
            Arg.Is<MeterRegressionPrompt>(p =>
                p.HouseholdId == householdId &&
                p.MainMeterId == mainMeter.Id &&
                p.MeterReadingId == result.Id &&
                p.PreviousMeterReadingId == preceding.Id &&
                p.Classification == null &&
                p.ResolvedAtUtc == null),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_reading_higher_than_the_immediately_preceding_one_does_not_raise_a_regression_prompt()
    {
        var householdId = Guid.NewGuid();
        var mainMeter = NewMainMeter(householdId);
        var preceding = new MeterReading
        {
            Id = Guid.NewGuid(),
            HouseholdId = householdId,
            MainMeterId = mainMeter.Id,
            KwhValue = 14302m,
            ReadingTimestamp = DateTimeOffset.UtcNow.AddDays(-1),
            IdempotencyKey = Guid.NewGuid(),
            CreatedAtUtc = DateTimeOffset.UtcNow.AddDays(-1),
        };
        _repository.FindByIdempotencyKeyAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns((MeterReading?)null);
        _repository.GetOrCreateMainMeterAsync(householdId, Arg.Any<CancellationToken>()).Returns(mainMeter);
        _repository.AddAsync(Arg.Any<MeterReading>(), Arg.Any<CancellationToken>()).Returns(callInfo => callInfo.Arg<MeterReading>());
        _repository.FindImmediatelyPrecedingAsync(mainMeter.Id, Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(preceding);
        var sut = Sut();

        await sut.ExecuteAsync(householdId, 14500m, DateTimeOffset.UtcNow, Guid.NewGuid(), TestContext.Current.CancellationToken);

        await _regressionPromptRepository.DidNotReceive().AddAsync(Arg.Any<MeterRegressionPrompt>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_reading_equal_to_the_immediately_preceding_one_does_not_raise_a_regression_prompt()
    {
        var householdId = Guid.NewGuid();
        var mainMeter = NewMainMeter(householdId);
        var preceding = new MeterReading
        {
            Id = Guid.NewGuid(),
            HouseholdId = householdId,
            MainMeterId = mainMeter.Id,
            KwhValue = 14302m,
            ReadingTimestamp = DateTimeOffset.UtcNow.AddDays(-1),
            IdempotencyKey = Guid.NewGuid(),
            CreatedAtUtc = DateTimeOffset.UtcNow.AddDays(-1),
        };
        _repository.FindByIdempotencyKeyAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns((MeterReading?)null);
        _repository.GetOrCreateMainMeterAsync(householdId, Arg.Any<CancellationToken>()).Returns(mainMeter);
        _repository.AddAsync(Arg.Any<MeterReading>(), Arg.Any<CancellationToken>()).Returns(callInfo => callInfo.Arg<MeterReading>());
        _repository.FindImmediatelyPrecedingAsync(mainMeter.Id, Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(preceding);
        var sut = Sut();

        await sut.ExecuteAsync(householdId, 14302m, DateTimeOffset.UtcNow, Guid.NewGuid(), TestContext.Current.CancellationToken);

        await _regressionPromptRepository.DidNotReceive().AddAsync(Arg.Any<MeterRegressionPrompt>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task The_first_ever_reading_for_a_Main_Meter_does_not_raise_a_regression_prompt()
    {
        var householdId = Guid.NewGuid();
        var mainMeter = NewMainMeter(householdId);
        _repository.FindByIdempotencyKeyAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns((MeterReading?)null);
        _repository.GetOrCreateMainMeterAsync(householdId, Arg.Any<CancellationToken>()).Returns(mainMeter);
        _repository.AddAsync(Arg.Any<MeterReading>(), Arg.Any<CancellationToken>()).Returns(callInfo => callInfo.Arg<MeterReading>());
        var sut = Sut();

        await sut.ExecuteAsync(householdId, 100m, DateTimeOffset.UtcNow, Guid.NewGuid(), TestContext.Current.CancellationToken);

        await _regressionPromptRepository.DidNotReceive().AddAsync(Arg.Any<MeterRegressionPrompt>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task An_idempotency_key_replay_does_not_re_run_regression_detection()
    {
        var householdId = Guid.NewGuid();
        var idempotencyKey = Guid.NewGuid();
        var existing = new MeterReading
        {
            Id = Guid.NewGuid(),
            HouseholdId = householdId,
            MainMeterId = Guid.NewGuid(),
            KwhValue = 4821.5m,
            ReadingTimestamp = DateTimeOffset.UtcNow,
            IdempotencyKey = idempotencyKey,
            CreatedAtUtc = DateTimeOffset.UtcNow,
        };
        _repository.FindByIdempotencyKeyAsync(idempotencyKey, Arg.Any<CancellationToken>()).Returns(existing);
        var sut = Sut();

        await sut.ExecuteAsync(householdId, 4821.5m, DateTimeOffset.UtcNow, idempotencyKey, TestContext.Current.CancellationToken);

        await _repository.DidNotReceive().FindImmediatelyPrecedingAsync(Arg.Any<Guid>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>());
        await _regressionPromptRepository.DidNotReceive().AddAsync(Arg.Any<MeterRegressionPrompt>(), Arg.Any<CancellationToken>());
    }
}
