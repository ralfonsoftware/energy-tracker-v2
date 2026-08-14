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

        // Postgres' timestamptz has microsecond precision, one tick coarser than
        // DateTimeOffset's 100ns ticks. Truncate before assigning so the in-memory value
        // returned from this call matches exactly what a later re-read from the DB
        // produces — otherwise a second archive call's guard-return would carry a
        // sub-microsecond-different ArchivedAt than the first call's response.
        var archivedAt = DateTimeOffset.UtcNow;
        room.ArchivedAt = archivedAt.AddTicks(-(archivedAt.Ticks % TimeSpan.TicksPerMicrosecond));
        await repository.UpdateRoomAsync(room, cancellationToken);

        return room;
    }
}
