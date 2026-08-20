using EnergyTracker.Application.Ports;
using EnergyTracker.Domain;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;

namespace EnergyTracker.Application.Tests;

public class MapSmartPlugImportToPowerPointTests
{
    private readonly ISmartPlugImportRepository _smartPlugImportRepository = Substitute.For<ISmartPlugImportRepository>();
    private readonly ITaggingScaffoldRepository _taggingScaffoldRepository = Substitute.For<ITaggingScaffoldRepository>();
    private readonly IStatusRecomputeService _statusRecomputeService = Substitute.For<IStatusRecomputeService>();
    private readonly Guid _householdId = Guid.NewGuid();

    public MapSmartPlugImportToPowerPointTests()
    {
        _smartPlugImportRepository.ListPriorReadingsByPowerPointAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<SmartPlugReading>)[]);
        _smartPlugImportRepository.FindFirstReadingDateByPowerPointAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns((DateOnly?)null);
    }

    private MapSmartPlugImportToPowerPoint Sut() => new(
        _smartPlugImportRepository, _taggingScaffoldRepository,
        new CompleteSmartPlugImportProcessing(_smartPlugImportRepository, _statusRecomputeService, NullLogger<CompleteSmartPlugImportProcessing>.Instance));

    private SmartPlugImport MakeImport(SmartPlugImportStatus status = SmartPlugImportStatus.AwaitingPowerPointMapping) => new()
    {
        Id = Guid.NewGuid(),
        HouseholdId = _householdId,
        BackgroundJobId = Guid.NewGuid(),
        VendorFormat = SmartPlugVendorFormat.EveHome,
        OriginalFileName = "export.xlsx",
        Status = status,
        DeviceTag = "Living Room Lamp",
        CreatedAtUtc = DateTimeOffset.UtcNow,
        CompletedAtUtc = null,
    };

    private SmartPlugReading MakeReading(Guid smartPlugImportId) => new()
    {
        Id = Guid.NewGuid(),
        HouseholdId = _householdId,
        SmartPlugImportId = smartPlugImportId,
        PowerPointId = null,
        RoomName = string.Empty,
        PowerPointName = "Living Room Lamp",
        DeviceName = "Living Room Lamp",
        IntervalStart = DateTimeOffset.UtcNow.AddHours(-1),
        IntervalEnd = DateTimeOffset.UtcNow,
        KwhValue = 0.5m,
    };

    private SmartPlugReading MakeReadingOnDate(Guid smartPlugImportId, DateOnly date) => new()
    {
        Id = Guid.NewGuid(),
        HouseholdId = _householdId,
        SmartPlugImportId = smartPlugImportId,
        PowerPointId = null,
        RoomName = string.Empty,
        PowerPointName = "Living Room Lamp",
        DeviceName = "Living Room Lamp",
        IntervalStart = new DateTimeOffset(date.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero),
        IntervalEnd = new DateTimeOffset(date.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero),
        KwhValue = 0.5m,
    };

    private Room MakeRoom(DateTimeOffset? archivedAt = null) => new()
    {
        Id = Guid.NewGuid(),
        HouseholdId = _householdId,
        Name = "Living Room",
        CreatedAtUtc = DateTimeOffset.UtcNow,
        ArchivedAt = archivedAt,
    };

    private PowerPoint MakePowerPoint(Guid roomId, DateTimeOffset? archivedAt = null) => new()
    {
        Id = Guid.NewGuid(),
        HouseholdId = _householdId,
        RoomId = roomId,
        Name = "Lamp outlet",
        CreatedAtUtc = DateTimeOffset.UtcNow,
        ArchivedAt = archivedAt,
    };

    [Fact]
    public async Task Sets_power_point_and_room_on_every_reading_and_completes_the_import()
    {
        var import = MakeImport();
        var readings = new List<SmartPlugReading> { MakeReading(import.Id), MakeReading(import.Id) };
        var room = MakeRoom();
        var powerPoint = MakePowerPoint(room.Id);
        _smartPlugImportRepository.FindByIdAsync(import.Id, Arg.Any<CancellationToken>()).Returns(import);
        _smartPlugImportRepository.ListReadingsByImportIdAsync(import.Id, Arg.Any<CancellationToken>()).Returns(readings);
        _taggingScaffoldRepository.FindPowerPointAsync(powerPoint.Id, Arg.Any<CancellationToken>()).Returns(powerPoint);
        _taggingScaffoldRepository.FindRoomAsync(room.Id, Arg.Any<CancellationToken>()).Returns(room);
        var sut = Sut();

        await sut.ExecuteAsync(import.Id, powerPoint.Id, TestContext.Current.CancellationToken);

        import.Status.ShouldBe(SmartPlugImportStatus.Completed);
        readings.ShouldAllBe(r => r.PowerPointId == powerPoint.Id && r.PowerPointName == powerPoint.Name && r.RoomName == room.Name);
        await _smartPlugImportRepository.Received(1).UpdateMappingAsync(
            Arg.Is<SmartPlugImport>(i => i.Id == import.Id && i.Status == SmartPlugImportStatus.Completed),
            Arg.Is<IReadOnlyList<SmartPlugReading>>(rs => rs.Count == readings.Count),
            Arg.Any<CancellationToken>());
        // AD-7's second completion path (Task 3) — Status recompute must fire here too.
        await _statusRecomputeService.Received(1).RecomputeAsync(_householdId, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Mapping_an_import_with_a_genuine_gap_also_persists_the_detected_gaps()
    {
        // Regression test for AD-7's second completion path (Task 3) at the unit level — the
        // Status-recompute half was already asserted above, but gap-detection wiring for this path
        // had no equivalent unit-level assertion.
        var import = MakeImport();
        var start = new DateOnly(2026, 8, 1);
        var readings = new List<SmartPlugReading>
        {
            MakeReadingOnDate(import.Id, start),
            // Aug 2 missing, Aug 3 has data (closes the gap) — well within the Power Point's first
            // week of history, so this is Missing, not Estimated; either way AddGapsAsync must fire.
            MakeReadingOnDate(import.Id, start.AddDays(2)),
        };
        var room = MakeRoom();
        var powerPoint = MakePowerPoint(room.Id);
        _smartPlugImportRepository.FindByIdAsync(import.Id, Arg.Any<CancellationToken>()).Returns(import);
        _smartPlugImportRepository.ListReadingsByImportIdAsync(import.Id, Arg.Any<CancellationToken>()).Returns(readings);
        _taggingScaffoldRepository.FindPowerPointAsync(powerPoint.Id, Arg.Any<CancellationToken>()).Returns(powerPoint);
        _taggingScaffoldRepository.FindRoomAsync(room.Id, Arg.Any<CancellationToken>()).Returns(room);
        var sut = Sut();

        await sut.ExecuteAsync(import.Id, powerPoint.Id, TestContext.Current.CancellationToken);

        await _smartPlugImportRepository.Received(1).AddGapsAsync(
            Arg.Is<IReadOnlyList<SmartPlugImportGap>>(gaps =>
                gaps.Count == 1 && gaps[0].StartDate == start.AddDays(1) && gaps[0].EndDate == start.AddDays(1)),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Throws_not_found_when_the_import_does_not_exist()
    {
        var smartPlugImportId = Guid.NewGuid();
        _smartPlugImportRepository.FindByIdAsync(smartPlugImportId, Arg.Any<CancellationToken>()).Returns((SmartPlugImport?)null);
        var sut = Sut();

        await Should.ThrowAsync<SmartPlugImportNotFoundException>(
            () => sut.ExecuteAsync(smartPlugImportId, Guid.NewGuid(), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Throws_validation_when_the_import_is_not_awaiting_mapping()
    {
        var import = MakeImport(status: SmartPlugImportStatus.Completed);
        _smartPlugImportRepository.FindByIdAsync(import.Id, Arg.Any<CancellationToken>()).Returns(import);
        var sut = Sut();

        await Should.ThrowAsync<SmartPlugImportValidationException>(
            () => sut.ExecuteAsync(import.Id, Guid.NewGuid(), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Throws_not_found_when_the_target_power_point_does_not_exist()
    {
        var import = MakeImport();
        var powerPointId = Guid.NewGuid();
        _smartPlugImportRepository.FindByIdAsync(import.Id, Arg.Any<CancellationToken>()).Returns(import);
        _taggingScaffoldRepository.FindPowerPointAsync(powerPointId, Arg.Any<CancellationToken>()).Returns((PowerPoint?)null);
        var sut = Sut();

        await Should.ThrowAsync<TaggingScaffoldNotFoundException>(
            () => sut.ExecuteAsync(import.Id, powerPointId, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Throws_parent_archived_when_the_target_power_point_is_archived()
    {
        var import = MakeImport();
        var room = MakeRoom();
        var powerPoint = MakePowerPoint(room.Id, archivedAt: DateTimeOffset.UtcNow);
        _smartPlugImportRepository.FindByIdAsync(import.Id, Arg.Any<CancellationToken>()).Returns(import);
        _taggingScaffoldRepository.FindPowerPointAsync(powerPoint.Id, Arg.Any<CancellationToken>()).Returns(powerPoint);
        var sut = Sut();

        await Should.ThrowAsync<TaggingScaffoldParentArchivedException>(
            () => sut.ExecuteAsync(import.Id, powerPoint.Id, TestContext.Current.CancellationToken));
    }
}
