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

        powerPoint.ArchivedAt = DateTimeOffset.UtcNow;
        await repository.UpdatePowerPointAsync(powerPoint, cancellationToken);

        return powerPoint;
    }
}
