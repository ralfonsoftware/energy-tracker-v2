using EnergyTracker.Application.Ports;
using EnergyTracker.Domain;

namespace EnergyTracker.Application;

/// <summary>Renames a Room (AC #2). Renaming an archived Room is allowed — no AC forbids it.</summary>
public class RenameRoom(ITaggingScaffoldRepository repository)
{
    public async Task<Room> ExecuteAsync(Guid roomId, string name, CancellationToken cancellationToken)
    {
        var room = await repository.FindRoomAsync(roomId, cancellationToken)
            ?? throw new TaggingScaffoldNotFoundException("Room", roomId);

        var validatedName = TaggingScaffoldNameValidator.Validate(name);

        var rooms = await repository.ListRoomsAsync(cancellationToken);
        if (rooms.Any(r => r.Id != roomId && string.Equals(r.Name, validatedName, StringComparison.Ordinal)))
        {
            throw new TaggingScaffoldValidationException($"A Room named '{validatedName}' already exists.");
        }

        room.Name = validatedName;

        await repository.UpdateRoomAsync(room, cancellationToken);

        return room;
    }
}
