using EnergyTracker.Application.Ports;
using EnergyTracker.Domain;
using NSubstitute;
using Shouldly;

namespace EnergyTracker.Application.Tests;

public class GetOpenMeterRegressionPromptTests
{
    private readonly IMeterRegressionPromptRepository _promptRepository = Substitute.For<IMeterRegressionPromptRepository>();
    private readonly IMeterReadingRepository _readingRepository = Substitute.For<IMeterReadingRepository>();

    private GetOpenMeterRegressionPrompt Sut() => new(_promptRepository, _readingRepository);

    [Fact]
    public async Task Returns_null_when_nothing_is_open()
    {
        var householdId = Guid.NewGuid();
        _promptRepository.GetOpenForHouseholdAsync(householdId, Arg.Any<CancellationToken>()).Returns((MeterRegressionPrompt?)null);
        var sut = Sut();

        var result = await sut.ExecuteAsync(householdId, TestContext.Current.CancellationToken);

        result.ShouldBeNull();
    }

    [Fact]
    public async Task Returns_the_enriched_details_when_one_is_open()
    {
        var householdId = Guid.NewGuid();
        var mainMeterId = Guid.NewGuid();
        var reading = new MeterReading
        {
            Id = Guid.NewGuid(),
            HouseholdId = householdId,
            MainMeterId = mainMeterId,
            KwhValue = 412m,
            ReadingTimestamp = DateTimeOffset.UtcNow,
            IdempotencyKey = Guid.NewGuid(),
            CreatedAtUtc = DateTimeOffset.UtcNow,
        };
        var previousReading = new MeterReading
        {
            Id = Guid.NewGuid(),
            HouseholdId = householdId,
            MainMeterId = mainMeterId,
            KwhValue = 14302m,
            ReadingTimestamp = DateTimeOffset.UtcNow.AddDays(-1),
            IdempotencyKey = Guid.NewGuid(),
            CreatedAtUtc = DateTimeOffset.UtcNow.AddDays(-1),
        };
        var prompt = new MeterRegressionPrompt
        {
            Id = Guid.NewGuid(),
            HouseholdId = householdId,
            MainMeterId = mainMeterId,
            MeterReadingId = reading.Id,
            PreviousMeterReadingId = previousReading.Id,
            CreatedAtUtc = DateTimeOffset.UtcNow,
        };
        _promptRepository.GetOpenForHouseholdAsync(householdId, Arg.Any<CancellationToken>()).Returns(prompt);
        _readingRepository.FindByIdAsync(reading.Id, Arg.Any<CancellationToken>()).Returns(reading);
        _readingRepository.FindByIdAsync(previousReading.Id, Arg.Any<CancellationToken>()).Returns(previousReading);
        _promptRepository.GetMainMeterDigitCapacityAsync(mainMeterId, Arg.Any<CancellationToken>()).Returns(99999m);
        var sut = Sut();

        var result = await sut.ExecuteAsync(householdId, TestContext.Current.CancellationToken);

        result.ShouldNotBeNull();
        result.Prompt.ShouldBe(prompt);
        result.Reading.ShouldBe(reading);
        result.PreviousReading.ShouldBe(previousReading);
        result.MainMeterDigitCapacityKwh.ShouldBe(99999m);
    }
}
