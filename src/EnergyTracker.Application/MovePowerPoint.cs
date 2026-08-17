using EnergyTracker.Application.Ports;
using EnergyTracker.Domain;

namespace EnergyTracker.Application;

/// <summary>Moves a Power Point to a different Room in the caller's own Household (AC #1, #4, #5, #6).</summary>
public class MovePowerPoint(ITaggingScaffoldRepository repository)
{
    public async Task<PowerPoint> ExecuteAsync(Guid powerPointId, Guid newRoomId, CancellationToken cancellationToken)
    {
        var powerPoint = await repository.FindPowerPointAsync(powerPointId, cancellationToken)
            ?? throw new TaggingScaffoldNotFoundException("PowerPoint", powerPointId);

        var siblings = await repository.ListPowerPointsAsync(cancellationToken);
        if (siblings.Any(p => p.Id != powerPointId && p.RoomId == newRoomId
            && string.Equals(p.Name, powerPoint.Name, StringComparison.Ordinal)))
        {
            throw new TaggingScaffoldValidationException($"A Power Point named '{powerPoint.Name}' already exists in this Room.");
        }

        var newRoom = await repository.FindRoomAsync(newRoomId, cancellationToken)
            ?? throw new TaggingScaffoldNotFoundException("Room", newRoomId);

        if (newRoom.ArchivedAt is not null)
        {
            throw new TaggingScaffoldParentArchivedException("Room", newRoomId);
        }

        powerPoint.RoomId = newRoomId;
        await repository.UpdatePowerPointAsync(powerPoint, cancellationToken);

        return powerPoint;
    }
}
