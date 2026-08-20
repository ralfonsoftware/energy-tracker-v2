using EnergyTracker.Application.Ports;
using EnergyTracker.Domain;

namespace EnergyTracker.Application;

/// <summary>Resolves a Smart Plug import parked AwaitingPowerPointMapping by attaching its readings to a Power Point (AC #1, #2, #3).</summary>
public class MapSmartPlugImportToPowerPoint(
    ISmartPlugImportRepository smartPlugImportRepository,
    ITaggingScaffoldRepository taggingScaffoldRepository,
    CompleteSmartPlugImportProcessing completeSmartPlugImportProcessing)
{
    public async Task ExecuteAsync(Guid smartPlugImportId, Guid powerPointId, CancellationToken cancellationToken)
    {
        var import = await smartPlugImportRepository.FindByIdAsync(smartPlugImportId, cancellationToken)
            ?? throw new SmartPlugImportNotFoundException(smartPlugImportId);

        if (import.Status != SmartPlugImportStatus.AwaitingPowerPointMapping)
        {
            throw new SmartPlugImportValidationException(
                $"Smart Plug import '{smartPlugImportId}' is not awaiting a Power Point mapping.");
        }

        var powerPoint = await taggingScaffoldRepository.FindPowerPointAsync(powerPointId, cancellationToken)
            ?? throw new TaggingScaffoldNotFoundException("PowerPoint", powerPointId);

        if (powerPoint.ArchivedAt is not null)
        {
            // Reusing TaggingScaffoldParentArchivedException for the target's own archived state
            // (not a parent-of-a-created-child, its usual case elsewhere) — its generic
            // "{type} '{id}' is archived" message and 409 mapping both still fit; see the
            // exception's own doc comment for why this second use is deliberate, not accidental.
            throw new TaggingScaffoldParentArchivedException("PowerPoint", powerPointId);
        }

        var room = await taggingScaffoldRepository.FindRoomAsync(powerPoint.RoomId, cancellationToken);

        var readings = await smartPlugImportRepository.ListReadingsByImportIdAsync(smartPlugImportId, cancellationToken);

        // AD-10: this mapping call is "write time" for these previously-unattributed readings —
        // snapshot the Power Point/Room identity by value now, never a live join later.
        foreach (var reading in readings)
        {
            reading.PowerPointId = powerPoint.Id;
            reading.PowerPointName = powerPoint.Name;
            reading.RoomName = room?.Name ?? reading.RoomName;
        }

        import.Status = SmartPlugImportStatus.Completed;
        import.CompletedAtUtc = DateTimeOffset.UtcNow;

        await smartPlugImportRepository.UpdateMappingAsync(import, readings, cancellationToken);

        // AD-7's second completion path (Story 3.2's own Dev Notes flagged this for this story) —
        // gap detection + Status recompute must fire here too, not just from
        // ProcessSmartPlugImport's direct-match branch.
        await completeSmartPlugImportProcessing.ExecuteAsync(import, readings, cancellationToken);
    }
}

public class SmartPlugImportNotFoundException(Guid id) : Exception($"SmartPlugImport '{id}' not found.");
