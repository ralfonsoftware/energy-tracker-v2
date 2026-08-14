using EnergyTracker.Application.Ports;
using EnergyTracker.Domain;

namespace EnergyTracker.Application;

/// <summary>
/// Soft-deletes a Room (AC #3). Idempotent — archiving an already-archived Room is a no-op, not
/// an error. Does not cascade-archive its Power Points (AD-10's "never cascade-delete" spirit
/// applies one level down too) — an archived Room's Power Points simply drop out of the
/// create-Power-Point picker (AC #4) while staying fully visible and editable themselves.
/// </summary>
public class ArchiveRoom(ITaggingScaffoldRepository repository)
{
    public async Task<Room> ExecuteAsync(Guid roomId, CancellationToken cancellationToken)
    {
        var room = await repository.FindRoomAsync(roomId, cancellationToken)
            ?? throw new TaggingScaffoldNotFoundException("Room", roomId);

        if (room.ArchivedAt is not null)
        {
            return room;
        }

        room.ArchivedAt = DateTimeOffset.UtcNow;
        await repository.UpdateRoomAsync(room, cancellationToken);

        return room;
    }
}
