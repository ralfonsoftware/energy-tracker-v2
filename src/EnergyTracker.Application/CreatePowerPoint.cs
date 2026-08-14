using EnergyTracker.Application.Ports;
using EnergyTracker.Domain;

namespace EnergyTracker.Application;

/// <summary>Creates a Power Point under a Room in the caller's own Household (AC #1, #4).</summary>
public class CreatePowerPoint(ITaggingScaffoldRepository repository)
{
    public async Task<PowerPoint> ExecuteAsync(Guid householdId, Guid roomId, string name, CancellationToken cancellationToken)
    {
        var validatedName = TaggingScaffoldNameValidator.Validate(name);

        var siblings = await repository.ListPowerPointsAsync(cancellationToken);
        if (siblings.Any(p => p.RoomId == roomId && string.Equals(p.Name, validatedName, StringComparison.Ordinal)))
        {
            throw new TaggingScaffoldValidationException($"A Power Point named '{validatedName}' already exists in this Room.");
        }

        var room = await repository.FindRoomAsync(roomId, cancellationToken)
            ?? throw new TaggingScaffoldNotFoundException("Room", roomId);

        if (room.ArchivedAt is not null)
        {
            throw new TaggingScaffoldParentArchivedException("Room", roomId);
        }

        var powerPoint = new PowerPoint
        {
            Id = Guid.NewGuid(),
            HouseholdId = householdId,
            RoomId = roomId,
            Name = validatedName,
            CreatedAtUtc = DateTimeOffset.UtcNow,
            ArchivedAt = null,
        };

        await repository.AddPowerPointAsync(powerPoint, cancellationToken);

        return powerPoint;
    }
}
