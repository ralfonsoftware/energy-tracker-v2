using EnergyTracker.Application.Ports;
using EnergyTracker.Domain;

namespace EnergyTracker.Application;

/// <summary>Creates a Room for the caller's own Household (AC #1). Rooms have no parent to validate.</summary>
public class CreateRoom(ITaggingScaffoldRepository repository)
{
    public async Task<Room> ExecuteAsync(Guid householdId, string name, CancellationToken cancellationToken)
    {
        var validatedName = TaggingScaffoldNameValidator.Validate(name);

        var rooms = await repository.ListRoomsAsync(cancellationToken);
        if (rooms.Any(r => string.Equals(r.Name, validatedName, StringComparison.Ordinal)))
        {
            throw new TaggingScaffoldValidationException($"A Room named '{validatedName}' already exists.");
        }

        var room = new Room
        {
            Id = Guid.NewGuid(),
            HouseholdId = householdId,
            Name = validatedName,
            CreatedAtUtc = DateTimeOffset.UtcNow,
            ArchivedAt = null,
        };

        await repository.AddRoomAsync(room, cancellationToken);

        return room;
    }
}
