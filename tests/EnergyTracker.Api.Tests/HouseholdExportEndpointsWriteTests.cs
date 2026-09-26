using System.IO.Pipelines;
using EnergyTracker.Api.Endpoints;
using EnergyTracker.Application;
using Shouldly;

namespace EnergyTracker.Api.Tests;

// Regression test for a bug caught in adversarial review (not the original design): the JSON writer
// used to flush only once per whole top-level collection. For one very large collection (the exact
// shape of the 2026-09-25 production incident — a household with huge SmartPlugReadings volume),
// that silently re-buffered the entire serialized array in the PipeWriter before ever flushing to
// the wire, reintroducing the OOM this fix exists to eliminate. HouseholdExportEndpoints.
// WriteExportAsync/FlushEveryNItems are `internal` (see their own comments) specifically so this
// test can assert the fix directly against a real System.IO.Pipelines.Pipe's backpressure — an
// HTTP-level test can't reliably tell "flushed periodically" apart from "buffered, then flushed
// once" once OS/transport buffering is in the mix.
public class HouseholdExportEndpointsWriteTests
{
    private static HouseholdSettingsExportDto NewHouseholdDto() => new(
        Guid.NewGuid(), DateTimeOffset.UtcNow, "de-DE", "EUR", 3500m, 300m, 30, 6, false, false, null);

    private static async IAsyncEnumerable<T> Empty<T>()
    {
        await Task.Yield();
        yield break;
    }

    private static async IAsyncEnumerable<SmartPlugReadingExportDto> ManyReadings(int count)
    {
        for (var i = 0; i < count; i++)
        {
            await Task.Yield();
            yield return new SmartPlugReadingExportDto(
                Guid.NewGuid(), Guid.NewGuid(), "Kitchen", "Outlet", "Kettle",
                DateTimeOffset.UtcNow.AddMinutes(-i), DateTimeOffset.UtcNow.AddMinutes(-i).AddMinutes(15), 0.5m);
        }
    }

    private static HouseholdExportStream NewStream(IAsyncEnumerable<SmartPlugReadingExportDto> smartPlugReadings) => new(
        FormatVersion: ExportHouseholdData.FormatVersion,
        ExportedAtUtc: DateTimeOffset.UtcNow,
        Household: NewHouseholdDto(),
        HouseholdMembers: Empty<HouseholdMemberExportDto>(),
        MainMeter: null,
        MeterReadings: Empty<MeterReadingExportDto>(),
        MeterRegressionPrompts: Empty<MeterRegressionPromptExportDto>(),
        Tariffs: Empty<TariffExportDto>(),
        Events: Empty<EventExportDto>(),
        Rooms: Empty<RoomExportDto>(),
        PowerPoints: Empty<PowerPointExportDto>(),
        Devices: Empty<DeviceExportDto>(),
        SmartPlugReadings: smartPlugReadings,
        StatusSnapshots: Empty<StatusSnapshotExportDto>(),
        AuditCorrections: Empty<AuditCorrectionExportDto>());

    [Fact]
    public async Task WriteExportAsync_applies_backpressure_instead_of_buffering_a_whole_large_collection_before_flushing()
    {
        // A pause threshold many times smaller than what ~10x FlushEveryNItems rows of serialized
        // JSON would occupy — if WriteExportAsync only flushed once at the end of the array (the
        // bug), it would never hit this threshold's FlushAsync-blocking behavior until fully done;
        // with the fix's periodic flush, it must block on FlushAsync long before finishing.
        var pipe = new Pipe(new PipeOptions(pauseWriterThreshold: 4 * 1024, resumeWriterThreshold: 2 * 1024));
        var rowCount = HouseholdExportEndpoints.FlushEveryNItems * 10;

        var writeTask = HouseholdExportEndpoints.WriteExportAsync(pipe.Writer, NewStream(ManyReadings(rowCount)), TestContext.Current.CancellationToken);

        var completedEarly = await Task.WhenAny(writeTask, Task.Delay(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken));
        completedEarly.ShouldNotBe(writeTask, "WriteExportAsync should have blocked on pipe backpressure partway through a large collection instead of buffering all of it before its first flush.");

        // Drain concurrently with the writer rather than gating the loop on writeTask.IsCompleted:
        // that check races the writer's own post-await continuation (the task can still read as
        // "not completed" for a moment after its final flush has already delivered the last bytes),
        // which can strand this loop inside a ReadAsync() call that will never return — no more
        // data is coming, and nothing calls pipe.Writer.Complete() to unblock it while stuck here.
        // Draining until the reader itself observes completion (only possible once Writer.Complete()
        // runs below, after the writer is provably done) has no such race.
        var totalRead = 0L;
        var drainTask = Task.Run(async () =>
        {
            while (true)
            {
                var readResult = await pipe.Reader.ReadAsync(TestContext.Current.CancellationToken);
                totalRead += readResult.Buffer.Length;
                pipe.Reader.AdvanceTo(readResult.Buffer.End);
                if (readResult.IsCompleted)
                {
                    return;
                }
            }
        }, TestContext.Current.CancellationToken);

        await writeTask;
        pipe.Writer.Complete();
        await drainTask;
        pipe.Reader.Complete();

        totalRead.ShouldBeGreaterThan(0);
    }
}
