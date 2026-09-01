using EnergyTracker.Application.Ports;
using EnergyTracker.Domain;
using NSubstitute;
using Shouldly;

namespace EnergyTracker.Application.Tests;

public class EditMeterReadingTests
{
    private readonly IMeterReadingRepository _readingRepository = Substitute.For<IMeterReadingRepository>();
    private readonly IAuditCorrectionRecorder _auditCorrectionRecorder = Substitute.For<IAuditCorrectionRecorder>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly IStatusRecomputeService _statusRecomputeService = Substitute.For<IStatusRecomputeService>();

    private EditMeterReading Sut()
    {
        // Pass-through: the real transaction wrapping is exercised by the API-layer Testcontainers
        // tests; here we just need the wrapped operation to actually run.
        _unitOfWork
            .ExecuteInTransactionAsync(Arg.Any<Func<CancellationToken, Task<MeterReading>>>(), Arg.Any<CancellationToken>())
            .Returns(callInfo => callInfo.Arg<Func<CancellationToken, Task<MeterReading>>>()(callInfo.Arg<CancellationToken>()));
        return new(_readingRepository, _auditCorrectionRecorder, _unitOfWork, _statusRecomputeService);
    }

    private static MeterReading NewReading(Guid householdId, decimal kwhValue, int version = 0, Guid? id = null) => new()
    {
        Id = id ?? Guid.NewGuid(),
        HouseholdId = householdId,
        MainMeterId = Guid.NewGuid(),
        KwhValue = kwhValue,
        ReadingTimestamp = DateTimeOffset.UtcNow,
        IdempotencyKey = Guid.NewGuid(),
        CreatedAtUtc = DateTimeOffset.UtcNow,
        Version = version,
    };

    [Fact]
    public async Task A_valid_edit_updates_KwhValue_and_increments_Version()
    {
        var householdId = Guid.NewGuid();
        var reading = NewReading(householdId, 100m, version: 3);
        var updated = NewReading(householdId, 150m, version: 4, id: reading.Id);
        _readingRepository.FindByIdAsync(reading.Id, Arg.Any<CancellationToken>()).Returns(reading);
        _readingRepository.UpdateKwhValueAsync(reading.Id, 150m, 3, Arg.Any<CancellationToken>()).Returns(updated);
        var sut = Sut();

        var result = await sut.ExecuteAsync(householdId, reading.Id, 150m, 3, TestContext.Current.CancellationToken);

        result.KwhValue.ShouldBe(150m);
        result.Version.ShouldBe(4);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task Rejects_a_kWh_value_that_is_not_positive(decimal kwhValue)
    {
        var sut = Sut();

        await Should.ThrowAsync<MeterReadingValidationException>(() =>
            sut.ExecuteAsync(Guid.NewGuid(), Guid.NewGuid(), kwhValue, 0, TestContext.Current.CancellationToken));

        await _readingRepository.DidNotReceive().UpdateKwhValueAsync(Arg.Any<Guid>(), Arg.Any<decimal>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Rejects_a_kWh_value_that_would_overflow_the_decimal_18_2_column()
    {
        var sut = Sut();

        // Same exact bound CreateMeterReadingTests asserts — catches drift between the two use
        // cases now that Task 3 extracted the shared validation.
        await Should.ThrowAsync<MeterReadingValidationException>(() =>
            sut.ExecuteAsync(Guid.NewGuid(), Guid.NewGuid(), 1_000_000_000_000_000m, 0, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Editing_a_non_existent_or_foreign_household_reading_throws_MeterReadingNotFoundException()
    {
        var readingId = Guid.NewGuid();
        _readingRepository.FindByIdAsync(readingId, Arg.Any<CancellationToken>()).Returns((MeterReading?)null);
        var sut = Sut();

        await Should.ThrowAsync<MeterReadingNotFoundException>(() =>
            sut.ExecuteAsync(Guid.NewGuid(), readingId, 100m, 0, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_stale_Version_throws_MeterReadingConcurrencyConflictException()
    {
        var householdId = Guid.NewGuid();
        var reading = NewReading(householdId, 100m, version: 3);
        _readingRepository.FindByIdAsync(reading.Id, Arg.Any<CancellationToken>()).Returns(reading);
        _readingRepository.UpdateKwhValueAsync(reading.Id, 150m, 2, Arg.Any<CancellationToken>())
            .Returns<MeterReading>(_ => throw new MeterReadingConcurrencyConflictException(reading.Id));
        var sut = Sut();

        await Should.ThrowAsync<MeterReadingConcurrencyConflictException>(() =>
            sut.ExecuteAsync(householdId, reading.Id, 150m, 2, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task RecordAsync_is_called_exactly_once_with_the_correct_old_and_new_values_on_a_real_change()
    {
        var householdId = Guid.NewGuid();
        var reading = NewReading(householdId, 100m, version: 3);
        var updated = NewReading(householdId, 150m, version: 4);
        _readingRepository.FindByIdAsync(reading.Id, Arg.Any<CancellationToken>()).Returns(reading);
        _readingRepository.UpdateKwhValueAsync(reading.Id, 150m, 3, Arg.Any<CancellationToken>()).Returns(updated);
        var sut = Sut();

        await sut.ExecuteAsync(householdId, reading.Id, 150m, 3, TestContext.Current.CancellationToken);

        await _auditCorrectionRecorder.Received(1).RecordAsync(
            householdId, "MeterReading", reading.Id, "KwhValue", "100", "150", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_no_op_save_of_the_same_value_skips_the_update_and_never_calls_RecordAsync()
    {
        var householdId = Guid.NewGuid();
        var reading = NewReading(householdId, 100m, version: 3);
        _readingRepository.FindByIdAsync(reading.Id, Arg.Any<CancellationToken>()).Returns(reading);
        var sut = Sut();

        var result = await sut.ExecuteAsync(householdId, reading.Id, 100m, 3, TestContext.Current.CancellationToken);

        // No write at all — not just no correction note — so Version isn't bumped for nothing,
        // which would otherwise hand a spurious 409 to anyone else holding the pre-edit Version.
        result.Version.ShouldBe(3);
        await _readingRepository.DidNotReceive().UpdateKwhValueAsync(
            Arg.Any<Guid>(), Arg.Any<decimal>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
        await _auditCorrectionRecorder.DidNotReceive().RecordAsync(
            Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        // Story 4.3 AC #3: a no-op save is not a correction — nothing changed for a forward
        // recompute to fix.
        await _statusRecomputeService.DidNotReceive().RecomputeForwardFromAsync(
            Arg.Any<Guid>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_real_change_triggers_a_forward_recompute_from_the_readings_own_CreatedAtUtc()
    {
        // Story 4.3 AC #3. Deliberately uses a backdated reading (CreatedAtUtc far after
        // ReadingTimestamp) to prove the call anchors on the wall-clock CreatedAtUtc, not the
        // domain ReadingTimestamp — the exact clock-type distinction the story's Dev Notes call out.
        var householdId = Guid.NewGuid();
        var createdAtUtc = DateTimeOffset.UtcNow;
        var reading = new MeterReading
        {
            Id = Guid.NewGuid(),
            HouseholdId = householdId,
            MainMeterId = Guid.NewGuid(),
            KwhValue = 100m,
            ReadingTimestamp = DateTimeOffset.UtcNow.AddDays(-30),
            IdempotencyKey = Guid.NewGuid(),
            CreatedAtUtc = createdAtUtc,
            Version = 3,
        };
        var updated = NewReading(householdId, 150m, version: 4, id: reading.Id);
        _readingRepository.FindByIdAsync(reading.Id, Arg.Any<CancellationToken>()).Returns(reading);
        _readingRepository.UpdateKwhValueAsync(reading.Id, 150m, 3, Arg.Any<CancellationToken>()).Returns(updated);
        var sut = Sut();

        await sut.ExecuteAsync(householdId, reading.Id, 150m, 3, TestContext.Current.CancellationToken);

        await _statusRecomputeService.Received(1).RecomputeForwardFromAsync(householdId, createdAtUtc, Arg.Any<CancellationToken>());
    }
}
