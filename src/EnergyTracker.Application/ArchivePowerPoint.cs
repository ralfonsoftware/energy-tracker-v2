using EnergyTracker.Application.Ports;
using EnergyTracker.Domain;

namespace EnergyTracker.Application;

/// <summary>
/// Soft-deletes a Power Point (AC #3). Idempotent — archiving an already-archived Power Point is
/// a no-op. Does not cascade-archive its Devices, matching ArchiveRoom's reasoning.
/// </summary>
public class ArchivePowerPoint(ITaggingScaffoldRepository repository)
{
    public async Task<PowerPoint> ExecuteAsync(Guid powerPointId, CancellationToken cancellationToken)
    {
        var powerPoint = await repository.FindPowerPointAsync(powerPointId, cancellationToken)
            ?? throw new TaggingScaffoldNotFoundException("PowerPoint", powerPointId);

        if (powerPoint.ArchivedAt is not null)
        {
            return powerPoint;
        }

        // See ArchiveRoom's identical truncation for why: keeps this call's in-memory
        // ArchivedAt byte-identical to what a later re-read from Postgres returns.
        var archivedAt = DateTimeOffset.UtcNow;
        powerPoint.ArchivedAt = archivedAt.AddTicks(-(archivedAt.Ticks % TimeSpan.TicksPerMicrosecond));
        await repository.UpdatePowerPointAsync(powerPoint, cancellationToken);

        return powerPoint;
    }
}
