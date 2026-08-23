using EnergyTracker.Application.Ports;
using EnergyTracker.Domain;
using NSubstitute;
using Shouldly;

namespace EnergyTracker.Application.Tests;

public class GetMeterReadingHistoryTests
{
    private readonly IMeterReadingRepository _readingRepository = Substitute.For<IMeterReadingRepository>();
    private readonly IMeterRegressionPromptRepository _regressionPromptRepository = Substitute.For<IMeterRegressionPromptRepository>();
    private readonly IAuditCorrectionRecorder _auditCorrectionRecorder = Substitute.For<IAuditCorrectionRecorder>();

    private GetMeterReadingHistory Sut() => new(_readingRepository, _regressionPromptRepository, _auditCorrectionRecorder);

    private static MainMeter NewMainMeter(Guid householdId) => new()
    {
        Id = Guid.NewGuid(),
        HouseholdId = householdId,
        CreatedAtUtc = DateTimeOffset.UtcNow,
    };

    private static MeterReading NewReading(Guid householdId, Guid mainMeterId, decimal kwhValue, DateTimeOffset timestamp) => new()
    {
        Id = Guid.NewGuid(),
        HouseholdId = householdId,
        MainMeterId = mainMeterId,
        KwhValue = kwhValue,
        ReadingTimestamp = timestamp,
        IdempotencyKey = Guid.NewGuid(),
        CreatedAtUtc = timestamp,
    };

    public GetMeterReadingHistoryTests()
    {
        _regressionPromptRepository.GetOpenForHouseholdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns((MeterRegressionPrompt?)null);
        _auditCorrectionRecorder.GetLatestForEntitiesAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, AuditCorrection>());
    }

    [Fact]
    public async Task Returns_an_empty_page_when_no_Main_Meter_exists_yet()
    {
        var householdId = Guid.NewGuid();
        _readingRepository.FindMainMeterByHouseholdAsync(householdId, Arg.Any<CancellationToken>()).Returns((MainMeter?)null);
        var sut = Sut();

        var result = await sut.ExecuteAsync(householdId, 1, 20, TestContext.Current.CancellationToken);

        result.TotalCount.ShouldBe(0);
        result.Items.ShouldBeEmpty();
        await _readingRepository.DidNotReceive().GetPageForMainMeterAsync(Arg.Any<Guid>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Returns_items_ordered_by_ReadingTimestamp_descending_as_supplied_by_the_repository()
    {
        var householdId = Guid.NewGuid();
        var mainMeter = NewMainMeter(householdId);
        var newer = NewReading(householdId, mainMeter.Id, 200m, DateTimeOffset.UtcNow);
        var older = NewReading(householdId, mainMeter.Id, 100m, DateTimeOffset.UtcNow.AddDays(-1));
        _readingRepository.FindMainMeterByHouseholdAsync(householdId, Arg.Any<CancellationToken>()).Returns(mainMeter);
        _readingRepository.GetPageForMainMeterAsync(mainMeter.Id, 1, 20, Arg.Any<CancellationToken>())
            .Returns((new List<MeterReading> { newer, older }, 2));
        var sut = Sut();

        var result = await sut.ExecuteAsync(householdId, 1, 20, TestContext.Current.CancellationToken);

        result.Items.Select(i => i.Reading.Id).ShouldBe([newer.Id, older.Id]);
    }

    [Fact]
    public async Task Pagination_math_is_correct_across_multiple_pages()
    {
        var householdId = Guid.NewGuid();
        var mainMeter = NewMainMeter(householdId);
        var readings = Enumerable.Range(0, 5).Select(i => NewReading(householdId, mainMeter.Id, i, DateTimeOffset.UtcNow.AddDays(-i))).ToList();
        _readingRepository.FindMainMeterByHouseholdAsync(householdId, Arg.Any<CancellationToken>()).Returns(mainMeter);
        _readingRepository.GetPageForMainMeterAsync(mainMeter.Id, 2, 2, Arg.Any<CancellationToken>())
            .Returns((readings.Skip(2).Take(2).ToList() as IReadOnlyList<MeterReading>, readings.Count));
        var sut = Sut();

        var result = await sut.ExecuteAsync(householdId, 2, 2, TestContext.Current.CancellationToken);

        result.Page.ShouldBe(2);
        result.PageSize.ShouldBe(2);
        result.TotalCount.ShouldBe(5);
        result.Items.Count.ShouldBe(2);
    }

    [Fact]
    public async Task A_reading_matching_the_open_regression_prompt_is_flagged_pending_others_are_not()
    {
        var householdId = Guid.NewGuid();
        var mainMeter = NewMainMeter(householdId);
        var flaggedReading = NewReading(householdId, mainMeter.Id, 100m, DateTimeOffset.UtcNow);
        var otherReading = NewReading(householdId, mainMeter.Id, 200m, DateTimeOffset.UtcNow.AddDays(-1));
        _readingRepository.FindMainMeterByHouseholdAsync(householdId, Arg.Any<CancellationToken>()).Returns(mainMeter);
        _readingRepository.GetPageForMainMeterAsync(mainMeter.Id, 1, 20, Arg.Any<CancellationToken>())
            .Returns((new List<MeterReading> { flaggedReading, otherReading }, 2));
        _regressionPromptRepository.GetOpenForHouseholdAsync(householdId, Arg.Any<CancellationToken>())
            .Returns(new MeterRegressionPrompt
            {
                Id = Guid.NewGuid(),
                HouseholdId = householdId,
                MainMeterId = mainMeter.Id,
                MeterReadingId = flaggedReading.Id,
                PreviousMeterReadingId = otherReading.Id,
                CreatedAtUtc = DateTimeOffset.UtcNow,
            });
        var sut = Sut();

        var result = await sut.ExecuteAsync(householdId, 1, 20, TestContext.Current.CancellationToken);

        result.Items.Single(i => i.Reading.Id == flaggedReading.Id).IsPendingRegression.ShouldBeTrue();
        result.Items.Single(i => i.Reading.Id == otherReading.Id).IsPendingRegression.ShouldBeFalse();
    }

    [Fact]
    public async Task A_reading_with_a_recorded_correction_surfaces_its_OldValue_one_without_does_not()
    {
        var householdId = Guid.NewGuid();
        var mainMeter = NewMainMeter(householdId);
        var correctedReading = NewReading(householdId, mainMeter.Id, 150m, DateTimeOffset.UtcNow);
        var uncorrectedReading = NewReading(householdId, mainMeter.Id, 200m, DateTimeOffset.UtcNow.AddDays(-1));
        var correction = new AuditCorrection
        {
            Id = Guid.NewGuid(),
            HouseholdId = householdId,
            EntityType = "MeterReading",
            EntityId = correctedReading.Id,
            FieldName = "KwhValue",
            OldValue = "100",
            NewValue = "150",
            CorrectedAtUtc = DateTimeOffset.UtcNow,
        };
        _readingRepository.FindMainMeterByHouseholdAsync(householdId, Arg.Any<CancellationToken>()).Returns(mainMeter);
        _readingRepository.GetPageForMainMeterAsync(mainMeter.Id, 1, 20, Arg.Any<CancellationToken>())
            .Returns((new List<MeterReading> { correctedReading, uncorrectedReading }, 2));
        _auditCorrectionRecorder.GetLatestForEntitiesAsync("MeterReading", Arg.Any<IReadOnlyList<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, AuditCorrection> { [correctedReading.Id] = correction });
        var sut = Sut();

        var result = await sut.ExecuteAsync(householdId, 1, 20, TestContext.Current.CancellationToken);

        result.Items.Single(i => i.Reading.Id == correctedReading.Id).LatestCorrection.ShouldBe(correction);
        result.Items.Single(i => i.Reading.Id == uncorrectedReading.Id).LatestCorrection.ShouldBeNull();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task Rejects_a_page_below_1(int page)
    {
        var sut = Sut();

        await Should.ThrowAsync<MeterReadingValidationException>(() =>
            sut.ExecuteAsync(Guid.NewGuid(), page, 20, TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(101)]
    public async Task Rejects_a_pageSize_outside_1_to_100(int pageSize)
    {
        var sut = Sut();

        await Should.ThrowAsync<MeterReadingValidationException>(() =>
            sut.ExecuteAsync(Guid.NewGuid(), 1, pageSize, TestContext.Current.CancellationToken));
    }
}
