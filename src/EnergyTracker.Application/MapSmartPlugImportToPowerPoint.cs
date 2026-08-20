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

        import.Status = SmartPlugImportStatus.Completed;
        import.CompletedAtUtc = DateTimeOffset.UtcNow;

        // AD-10: this mapping call is "write time" for these previously-unattributed readings —
        // snapshot the Power Point/Room identity by value now, never a live join later. A
        // set-based UPDATE (not load-every-row-then-mutate) — see UpdateMappingAsync's doc comment.
        await smartPlugImportRepository.UpdateMappingAsync(import, powerPoint.Id, powerPoint.Name, room?.Name, cancellationToken);

        // Read back only now, after the UPDATE above already persisted the Power Point/Room
        // attribution — gap detection needs these readings' own values (kWh, timestamps), not
        // further mutation, so this is a plain read.
        var readings = await smartPlugImportRepository.ListReadingsByImportIdAsync(smartPlugImportId, cancellationToken);

        // AD-7's second completion path (Story 3.2's own Dev Notes flagged this for this story) —
        // gap detection + Status recompute must fire here too, not just from
        // ProcessSmartPlugImport's direct-match branch.
        await completeSmartPlugImportProcessing.ExecuteAsync(import, readings, cancellationToken);
    }
}

public class SmartPlugImportNotFoundException(Guid id) : Exception($"SmartPlugImport '{id}' not found.");
