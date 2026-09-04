using EnergyTracker.Application.Ports;
using EnergyTracker.Domain;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;

namespace EnergyTracker.Application.Tests;

// No unit-level test class existed for ProcessSmartPlugImport before this story — only indirectly
// covered via SmartPlugImportEndpointsTests (Testcontainers, through the real HTTP endpoint +
// background job). Story 3.4 adds real branching logic (header-first flow, watermark resolution,
// zero-new-rows disambiguation) worth a fast unit-level regression guard. Follows
// MapSmartPlugImportToPowerPointTests's exact pattern: CompleteSmartPlugImportProcessing is a
// plain class (Story 3.3's own deliberate design, no interface, so it can't be substituted
// directly) — construct a real one wired with substituted ports and assert indirectly via those
// substitutes.
public class ProcessSmartPlugImportTests
{
    private readonly ISmartPlugImportRepository _smartPlugImportRepository = Substitute.For<ISmartPlugImportRepository>();
    private readonly ITaggingScaffoldRepository _taggingScaffoldRepository = Substitute.For<ITaggingScaffoldRepository>();
    private readonly IStatusRecomputeService _statusRecomputeService = Substitute.For<IStatusRecomputeService>();
    private readonly ISmartPlugParser _parser = Substitute.For<ISmartPlugParser>();
    private readonly IAuditCorrectionRecorder _auditCorrectionRecorder = Substitute.For<IAuditCorrectionRecorder>();
    private readonly Guid _householdId = Guid.NewGuid();
    private readonly string _tempFilePath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.xlsx");

    public ProcessSmartPlugImportTests()
    {
        File.WriteAllText(_tempFilePath, "placeholder — the parser is substituted, its content is never read");

        _smartPlugImportRepository.ListPriorReadingsByPowerPointAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<SmartPlugReading>)[]);
        _smartPlugImportRepository.FindFirstReadingDateByPowerPointAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns((DateOnly?)null);

        _parser.CanParse(Arg.Any<string>()).Returns(true);
        _parser.Vendor.Returns(SmartPlugVendorFormat.EveHome);
    }

    private ProcessSmartPlugImport Sut() => new(
        [_parser], _taggingScaffoldRepository, _smartPlugImportRepository,
        new CompleteSmartPlugImportProcessing(_smartPlugImportRepository, _statusRecomputeService, NullLogger<CompleteSmartPlugImportProcessing>.Instance),
        _auditCorrectionRecorder, NullLogger<ProcessSmartPlugImport>.Instance);

    private ProcessSmartPlugImportPayload MakePayload(string deviceTag) =>
        new(Guid.NewGuid(), _tempFilePath, $"{deviceTag}.xlsx");

    private Room MakeRoom() => new()
    {
        Id = Guid.NewGuid(),
        HouseholdId = _householdId,
        Name = "Living Room",
        CreatedAtUtc = DateTimeOffset.UtcNow,
    };

    private PowerPoint MakePowerPoint(Guid roomId, string name) => new()
    {
        Id = Guid.NewGuid(),
        HouseholdId = _householdId,
        RoomId = roomId,
        Name = name,
        CreatedAtUtc = DateTimeOffset.UtcNow,
    };

    private SmartPlugReading MakeReading(string deviceTag, DateTimeOffset intervalStart, decimal kwhValue = 0.5m) => new()
    {
        Id = Guid.NewGuid(),
        HouseholdId = Guid.Empty,
        SmartPlugImportId = Guid.Empty,
        PowerPointId = null,
        RoomName = string.Empty,
        PowerPointName = deviceTag,
        DeviceName = deviceTag,
        IntervalStart = intervalStart,
        IntervalEnd = intervalStart,
        KwhValue = kwhValue,
    };

    [Fact]
    public async Task A_matched_power_point_with_prior_readings_invokes_the_parser_with_a_non_null_watermark()
    {
        const string deviceTag = "Living Room Lamp";
        var room = MakeRoom();
        var powerPoint = MakePowerPoint(room.Id, deviceTag);
        var watermark = new SmartPlugReadingWatermark(Guid.NewGuid(), DateTimeOffset.UtcNow.AddDays(-1), 0.5m);
        var payload = MakePayload(deviceTag);

        _parser.ReadDeviceTag(Arg.Any<Stream>(), payload.OriginalFileName, Arg.Any<CancellationToken>()).Returns(deviceTag);
        _taggingScaffoldRepository.ListPowerPointsAsync(Arg.Any<CancellationToken>()).Returns((IReadOnlyList<PowerPoint>)[powerPoint]);
        _taggingScaffoldRepository.FindRoomAsync(room.Id, Arg.Any<CancellationToken>()).Returns(room);
        _smartPlugImportRepository.FindLatestReadingIntervalStartByPowerPointAsync(powerPoint.Id, Arg.Any<CancellationToken>())
            .Returns(watermark);
        _parser.Parse(Arg.Any<Stream>(), payload.OriginalFileName, Arg.Any<DateTimeOffset?>(), Arg.Any<CancellationToken>())
            .Returns(new SmartPlugParseResult([MakeReading(deviceTag, DateTimeOffset.UtcNow)], RawDataRowsRead: 1));
        var sut = Sut();

        await sut.ExecuteAsync(_householdId, Guid.NewGuid(), payload, TestContext.Current.CancellationToken);

        _parser.Received(1).Parse(Arg.Any<Stream>(), payload.OriginalFileName, watermark.IntervalStart, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_matched_power_point_with_prior_readings_and_zero_new_rows_completes_with_no_readings_and_skips_status_recompute()
    {
        // Regression guard for Task 5's disambiguation: a "nothing new" incremental re-import
        // (watermark not null, zero rows parsed) must NOT be treated as Story 3.3's AC #7
        // "entirely gaps" case, and must not trigger gap detection/Status recompute.
        const string deviceTag = "Living Room Lamp";
        var room = MakeRoom();
        var powerPoint = MakePowerPoint(room.Id, deviceTag);
        var watermark = new SmartPlugReadingWatermark(Guid.NewGuid(), DateTimeOffset.UtcNow.AddDays(-1), 0.5m);
        var payload = MakePayload(deviceTag);

        _parser.ReadDeviceTag(Arg.Any<Stream>(), payload.OriginalFileName, Arg.Any<CancellationToken>()).Returns(deviceTag);
        _taggingScaffoldRepository.ListPowerPointsAsync(Arg.Any<CancellationToken>()).Returns((IReadOnlyList<PowerPoint>)[powerPoint]);
        _taggingScaffoldRepository.FindRoomAsync(room.Id, Arg.Any<CancellationToken>()).Returns(room);
        _smartPlugImportRepository.FindLatestReadingIntervalStartByPowerPointAsync(powerPoint.Id, Arg.Any<CancellationToken>())
            .Returns(watermark);
        _parser.Parse(Arg.Any<Stream>(), payload.OriginalFileName, Arg.Any<DateTimeOffset?>(), Arg.Any<CancellationToken>())
            .Returns(new SmartPlugParseResult([], RawDataRowsRead: 3));
        var sut = Sut();

        await sut.ExecuteAsync(_householdId, Guid.NewGuid(), payload, TestContext.Current.CancellationToken);

        await _smartPlugImportRepository.Received(1).AddAsync(
            Arg.Is<SmartPlugImport>(i => i.Status == SmartPlugImportStatus.Completed && i.DeviceTag == deviceTag),
            Arg.Is<IReadOnlyList<SmartPlugReading>>(r => r.Count == 0),
            Arg.Any<CancellationToken>());
        await _statusRecomputeService.DidNotReceive().RecomputeAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_matched_power_point_with_prior_readings_and_zero_raw_rows_read_is_flagged_for_review_not_completed()
    {
        // Review-round-2 patch regression guard: a genuinely corrupt/truncated re-upload (the
        // parser read zero raw data rows, not just zero rows surviving the watermark filter) must
        // still surface as FlaggedForReview, not be silently indistinguishable from a legitimate
        // "nothing new" re-import.
        const string deviceTag = "Living Room Lamp";
        var room = MakeRoom();
        var powerPoint = MakePowerPoint(room.Id, deviceTag);
        var watermark = new SmartPlugReadingWatermark(Guid.NewGuid(), DateTimeOffset.UtcNow.AddDays(-1), 0.5m);
        var payload = MakePayload(deviceTag);

        _parser.ReadDeviceTag(Arg.Any<Stream>(), payload.OriginalFileName, Arg.Any<CancellationToken>()).Returns(deviceTag);
        _taggingScaffoldRepository.ListPowerPointsAsync(Arg.Any<CancellationToken>()).Returns((IReadOnlyList<PowerPoint>)[powerPoint]);
        _taggingScaffoldRepository.FindRoomAsync(room.Id, Arg.Any<CancellationToken>()).Returns(room);
        _smartPlugImportRepository.FindLatestReadingIntervalStartByPowerPointAsync(powerPoint.Id, Arg.Any<CancellationToken>())
            .Returns(watermark);
        _parser.Parse(Arg.Any<Stream>(), payload.OriginalFileName, Arg.Any<DateTimeOffset?>(), Arg.Any<CancellationToken>())
            .Returns(new SmartPlugParseResult([], RawDataRowsRead: 0));
        var sut = Sut();

        await sut.ExecuteAsync(_householdId, Guid.NewGuid(), payload, TestContext.Current.CancellationToken);

        await _smartPlugImportRepository.Received(1).AddFlaggedForReviewAsync(
            Arg.Is<SmartPlugImport>(i => i.Status == SmartPlugImportStatus.FlaggedForReview && i.DeviceTag == deviceTag),
            Arg.Any<SmartPlugImportGap>(),
            Arg.Any<CancellationToken>());
        await _smartPlugImportRepository.DidNotReceive().AddAsync(
            Arg.Any<SmartPlugImport>(), Arg.Any<IReadOnlyList<SmartPlugReading>>(), Arg.Any<CancellationToken>());
        await _statusRecomputeService.DidNotReceive().RecomputeAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task An_unmatched_device_tag_invokes_the_parser_with_a_null_watermark()
    {
        // AC #4: AwaitingPowerPointMapping (no match) parses the full file exactly as before this
        // story — no watermark lookup happens without a resolved Power Point.
        const string deviceTag = "Unknown Plug";
        var payload = MakePayload(deviceTag);

        _parser.ReadDeviceTag(Arg.Any<Stream>(), payload.OriginalFileName, Arg.Any<CancellationToken>()).Returns(deviceTag);
        _taggingScaffoldRepository.ListPowerPointsAsync(Arg.Any<CancellationToken>()).Returns((IReadOnlyList<PowerPoint>)[]);
        _parser.Parse(Arg.Any<Stream>(), payload.OriginalFileName, Arg.Any<DateTimeOffset?>(), Arg.Any<CancellationToken>())
            .Returns(new SmartPlugParseResult([MakeReading(deviceTag, DateTimeOffset.UtcNow)], RawDataRowsRead: 1));
        var sut = Sut();

        await sut.ExecuteAsync(_householdId, Guid.NewGuid(), payload, TestContext.Current.CancellationToken);

        _parser.Received(1).Parse(Arg.Any<Stream>(), payload.OriginalFileName, null, Arg.Any<CancellationToken>());
        await _smartPlugImportRepository.Received(1).AddAsync(
            Arg.Is<SmartPlugImport>(i => i.Status == SmartPlugImportStatus.AwaitingPowerPointMapping),
            Arg.Any<IReadOnlyList<SmartPlugReading>>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_matched_power_point_with_no_prior_readings_invokes_the_parser_with_a_null_watermark()
    {
        // AC #4: a first-ever import for an already-matched Power Point also parses in full.
        const string deviceTag = "Living Room Lamp";
        var room = MakeRoom();
        var powerPoint = MakePowerPoint(room.Id, deviceTag);
        var payload = MakePayload(deviceTag);

        _parser.ReadDeviceTag(Arg.Any<Stream>(), payload.OriginalFileName, Arg.Any<CancellationToken>()).Returns(deviceTag);
        _taggingScaffoldRepository.ListPowerPointsAsync(Arg.Any<CancellationToken>()).Returns((IReadOnlyList<PowerPoint>)[powerPoint]);
        _taggingScaffoldRepository.FindRoomAsync(room.Id, Arg.Any<CancellationToken>()).Returns(room);
        _smartPlugImportRepository.FindLatestReadingIntervalStartByPowerPointAsync(powerPoint.Id, Arg.Any<CancellationToken>())
            .Returns((SmartPlugReadingWatermark?)null);
        _parser.Parse(Arg.Any<Stream>(), payload.OriginalFileName, Arg.Any<DateTimeOffset?>(), Arg.Any<CancellationToken>())
            .Returns(new SmartPlugParseResult([MakeReading(deviceTag, DateTimeOffset.UtcNow)], RawDataRowsRead: 1));
        var sut = Sut();

        await sut.ExecuteAsync(_householdId, Guid.NewGuid(), payload, TestContext.Current.CancellationToken);

        _parser.Received(1).Parse(Arg.Any<Stream>(), payload.OriginalFileName, null, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task An_exact_re_reported_boundary_row_is_dropped_without_a_correction_or_repository_update()
    {
        // AD-22 AC #5: the boundary row's KwhValue matches the stored watermark value exactly —
        // nothing to write, no audit correction, and it must never reach AddAsync as a "new" row.
        const string deviceTag = "Living Room Lamp";
        var room = MakeRoom();
        var powerPoint = MakePowerPoint(room.Id, deviceTag);
        var boundaryIntervalStart = DateTimeOffset.UtcNow.AddDays(-1);
        var watermark = new SmartPlugReadingWatermark(Guid.NewGuid(), boundaryIntervalStart, 0.5m);
        var payload = MakePayload(deviceTag);

        _parser.ReadDeviceTag(Arg.Any<Stream>(), payload.OriginalFileName, Arg.Any<CancellationToken>()).Returns(deviceTag);
        _taggingScaffoldRepository.ListPowerPointsAsync(Arg.Any<CancellationToken>()).Returns((IReadOnlyList<PowerPoint>)[powerPoint]);
        _taggingScaffoldRepository.FindRoomAsync(room.Id, Arg.Any<CancellationToken>()).Returns(room);
        _smartPlugImportRepository.FindLatestReadingIntervalStartByPowerPointAsync(powerPoint.Id, Arg.Any<CancellationToken>())
            .Returns(watermark);
        _parser.Parse(Arg.Any<Stream>(), payload.OriginalFileName, Arg.Any<DateTimeOffset?>(), Arg.Any<CancellationToken>())
            .Returns(new SmartPlugParseResult([MakeReading(deviceTag, boundaryIntervalStart, kwhValue: 0.5m)], RawDataRowsRead: 1));
        var sut = Sut();

        await sut.ExecuteAsync(_householdId, Guid.NewGuid(), payload, TestContext.Current.CancellationToken);

        await _smartPlugImportRepository.DidNotReceive().UpdateReadingKwhValueAsync(
            Arg.Any<Guid>(), Arg.Any<decimal>(), Arg.Any<CancellationToken>());
        await _auditCorrectionRecorder.DidNotReceive().RecordAsync(
            Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        // The exact re-report was the only parsed row — it's dropped, so this becomes a "nothing
        // new" Completed import with zero readings, same as Story 3.4's own disambiguation.
        await _smartPlugImportRepository.Received(1).AddAsync(
            Arg.Is<SmartPlugImport>(i => i.Status == SmartPlugImportStatus.Completed),
            Arg.Is<IReadOnlyList<SmartPlugReading>>(r => r.Count == 0),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_divergent_boundary_row_triggers_a_narrow_KwhValue_correction_and_an_audit_record()
    {
        // AD-22 AC #5/#6/#11: the boundary row's KwhValue differs from the stored watermark value
        // — the existing stored row is updated by the watermark's own Id, and the correction is
        // recorded via IAuditCorrectionRecorder with the correct entityType/entityId/fieldName and
        // old/new values; the row still never reaches AddAsync as a "new" row.
        const string deviceTag = "Living Room Lamp";
        var room = MakeRoom();
        var powerPoint = MakePowerPoint(room.Id, deviceTag);
        var boundaryIntervalStart = DateTimeOffset.UtcNow.AddDays(-1);
        var watermark = new SmartPlugReadingWatermark(Guid.NewGuid(), boundaryIntervalStart, 0.5m);
        var payload = MakePayload(deviceTag);

        _parser.ReadDeviceTag(Arg.Any<Stream>(), payload.OriginalFileName, Arg.Any<CancellationToken>()).Returns(deviceTag);
        _taggingScaffoldRepository.ListPowerPointsAsync(Arg.Any<CancellationToken>()).Returns((IReadOnlyList<PowerPoint>)[powerPoint]);
        _taggingScaffoldRepository.FindRoomAsync(room.Id, Arg.Any<CancellationToken>()).Returns(room);
        _smartPlugImportRepository.FindLatestReadingIntervalStartByPowerPointAsync(powerPoint.Id, Arg.Any<CancellationToken>())
            .Returns(watermark);
        _parser.Parse(Arg.Any<Stream>(), payload.OriginalFileName, Arg.Any<DateTimeOffset?>(), Arg.Any<CancellationToken>())
            .Returns(new SmartPlugParseResult([MakeReading(deviceTag, boundaryIntervalStart, kwhValue: 0.75m)], RawDataRowsRead: 1));
        var sut = Sut();

        await sut.ExecuteAsync(_householdId, Guid.NewGuid(), payload, TestContext.Current.CancellationToken);

        await _smartPlugImportRepository.Received(1).UpdateReadingKwhValueAsync(watermark.Id, 0.75m, Arg.Any<CancellationToken>());
        await _auditCorrectionRecorder.Received(1).RecordAsync(
            _householdId, "SmartPlugReading", watermark.Id, "KwhValue", "0.5", "0.75", Arg.Any<CancellationToken>());
        await _smartPlugImportRepository.Received(1).AddAsync(
            Arg.Is<SmartPlugImport>(i => i.Status == SmartPlugImportStatus.Completed),
            Arg.Is<IReadOnlyList<SmartPlugReading>>(r => r.Count == 0),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Multiple_rows_at_the_watermark_boundary_are_all_dropped_with_at_most_one_correction_attempt()
    {
        // AD-22 AC #7 (DST-fold discipline): more than one parsed row shares the exact watermark
        // IntervalStart — only the first-encountered is compared/corrected, every row sharing that
        // IntervalStart is dropped, and at most one correction is ever attempted.
        const string deviceTag = "Living Room Lamp";
        var room = MakeRoom();
        var powerPoint = MakePowerPoint(room.Id, deviceTag);
        var boundaryIntervalStart = DateTimeOffset.UtcNow.AddDays(-1);
        var watermark = new SmartPlugReadingWatermark(Guid.NewGuid(), boundaryIntervalStart, 0.5m);
        var payload = MakePayload(deviceTag);
        var firstDuplicate = MakeReading(deviceTag, boundaryIntervalStart, kwhValue: 0.75m);
        var secondDuplicate = MakeReading(deviceTag, boundaryIntervalStart, kwhValue: 0.9m);
        var otherRow = MakeReading(deviceTag, boundaryIntervalStart.AddMinutes(10), kwhValue: 0.2m);

        _parser.ReadDeviceTag(Arg.Any<Stream>(), payload.OriginalFileName, Arg.Any<CancellationToken>()).Returns(deviceTag);
        _taggingScaffoldRepository.ListPowerPointsAsync(Arg.Any<CancellationToken>()).Returns((IReadOnlyList<PowerPoint>)[powerPoint]);
        _taggingScaffoldRepository.FindRoomAsync(room.Id, Arg.Any<CancellationToken>()).Returns(room);
        _smartPlugImportRepository.FindLatestReadingIntervalStartByPowerPointAsync(powerPoint.Id, Arg.Any<CancellationToken>())
            .Returns(watermark);
        _parser.Parse(Arg.Any<Stream>(), payload.OriginalFileName, Arg.Any<DateTimeOffset?>(), Arg.Any<CancellationToken>())
            .Returns(new SmartPlugParseResult([firstDuplicate, secondDuplicate, otherRow], RawDataRowsRead: 3));
        var sut = Sut();

        await sut.ExecuteAsync(_householdId, Guid.NewGuid(), payload, TestContext.Current.CancellationToken);

        // Only the first-encountered duplicate's value (0.75) is ever compared/corrected against.
        await _smartPlugImportRepository.Received(1).UpdateReadingKwhValueAsync(watermark.Id, 0.75m, Arg.Any<CancellationToken>());
        await _auditCorrectionRecorder.Received(1).RecordAsync(
            Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        // Both boundary-sharing rows are dropped; only the genuinely new, later-timestamped row
        // survives into the batch AddAsync eventually persists.
        await _smartPlugImportRepository.Received(1).AddAsync(
            Arg.Any<SmartPlugImport>(),
            Arg.Is<IReadOnlyList<SmartPlugReading>>(r => r.Count == 1 && r[0].IntervalStart == otherRow.IntervalStart),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_boundary_correction_never_touches_RoomName_PowerPointName_or_DeviceName()
    {
        // AD-22 AC #6/AD-10 regression guard: the correction call is a narrow KwhValue-only
        // update — the repository method it calls takes no RoomName/PowerPointName/DeviceName
        // argument at all, so this asserts the exact overload/arguments used carry only the
        // reading Id and the new KwhValue.
        const string deviceTag = "Living Room Lamp";
        var room = MakeRoom();
        var powerPoint = MakePowerPoint(room.Id, deviceTag);
        var boundaryIntervalStart = DateTimeOffset.UtcNow.AddDays(-1);
        var watermark = new SmartPlugReadingWatermark(Guid.NewGuid(), boundaryIntervalStart, 0.5m);
        var payload = MakePayload(deviceTag);

        _parser.ReadDeviceTag(Arg.Any<Stream>(), payload.OriginalFileName, Arg.Any<CancellationToken>()).Returns(deviceTag);
        _taggingScaffoldRepository.ListPowerPointsAsync(Arg.Any<CancellationToken>()).Returns((IReadOnlyList<PowerPoint>)[powerPoint]);
        _taggingScaffoldRepository.FindRoomAsync(room.Id, Arg.Any<CancellationToken>()).Returns(room);
        _smartPlugImportRepository.FindLatestReadingIntervalStartByPowerPointAsync(powerPoint.Id, Arg.Any<CancellationToken>())
            .Returns(watermark);
        _parser.Parse(Arg.Any<Stream>(), payload.OriginalFileName, Arg.Any<DateTimeOffset?>(), Arg.Any<CancellationToken>())
            .Returns(new SmartPlugParseResult([MakeReading(deviceTag, boundaryIntervalStart, kwhValue: 0.75m)], RawDataRowsRead: 1));
        var sut = Sut();

        await sut.ExecuteAsync(_householdId, Guid.NewGuid(), payload, TestContext.Current.CancellationToken);

        // ISmartPlugImportRepository.UpdateReadingKwhValueAsync's own signature (Guid readingId,
        // decimal newKwhValue, CancellationToken) structurally cannot carry a RoomName/
        // PowerPointName/DeviceName argument — receiving exactly this call is the regression guard.
        await _smartPlugImportRepository.Received(1).UpdateReadingKwhValueAsync(watermark.Id, 0.75m, Arg.Any<CancellationToken>());
    }
}
