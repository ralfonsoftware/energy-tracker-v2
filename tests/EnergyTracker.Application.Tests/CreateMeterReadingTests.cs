using EnergyTracker.Application.Ports;
using EnergyTracker.Domain;
using NSubstitute;
using Shouldly;

namespace EnergyTracker.Application.Tests;

public class CreateMeterReadingTests
{
    private readonly IMeterReadingRepository _repository = Substitute.For<IMeterReadingRepository>();

    private static MainMeter NewMainMeter(Guid householdId) => new()
    {
        Id = Guid.NewGuid(),
        HouseholdId = householdId,
        CreatedAtUtc = DateTimeOffset.UtcNow,
    };

    [Fact]
    public async Task Creates_and_persists_a_Meter_Reading_for_the_callers_own_Household()
    {
        var householdId = Guid.NewGuid();
        var mainMeter = NewMainMeter(householdId);
        _repository.FindByIdempotencyKeyAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns((MeterReading?)null);
        _repository.GetOrCreateMainMeterAsync(householdId, Arg.Any<CancellationToken>()).Returns(mainMeter);
        _repository.AddAsync(Arg.Any<MeterReading>(), Arg.Any<CancellationToken>()).Returns(callInfo => callInfo.Arg<MeterReading>());
        var sut = new CreateMeterReading(_repository);
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
        var sut = new CreateMeterReading(_repository);

        await Should.ThrowAsync<MeterReadingValidationException>(() =>
            sut.ExecuteAsync(Guid.NewGuid(), kwhValue, DateTimeOffset.UtcNow, Guid.NewGuid(), TestContext.Current.CancellationToken));

        await _repository.DidNotReceive().AddAsync(Arg.Any<MeterReading>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Rejects_a_kWh_value_that_would_overflow_the_decimal_18_2_column()
    {
        var sut = new CreateMeterReading(_repository);

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
        var sut = new CreateMeterReading(_repository);

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
        var sut = new CreateMeterReading(_repository);

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
        var sut = new CreateMeterReading(_repository);

        await sut.ExecuteAsync(householdId, 100m, DateTimeOffset.UtcNow, Guid.NewGuid(), TestContext.Current.CancellationToken);

        await _repository.Received(1).GetOrCreateMainMeterAsync(householdId, Arg.Any<CancellationToken>());
    }
}
