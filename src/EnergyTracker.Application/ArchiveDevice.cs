using EnergyTracker.Application.Ports;
using EnergyTracker.Domain;

namespace EnergyTracker.Application;

/// <summary>Soft-deletes a Device (AC #3). Idempotent — archiving an already-archived Device is a no-op.</summary>
public class ArchiveDevice(ITaggingScaffoldRepository repository)
{
    public async Task<Device> ExecuteAsync(Guid deviceId, CancellationToken cancellationToken)
    {
        var device = await repository.FindDeviceAsync(deviceId, cancellationToken)
            ?? throw new TaggingScaffoldNotFoundException("Device", deviceId);

        if (device.ArchivedAt is not null)
        {
            return device;
        }

        // See ArchiveRoom's identical truncation for why: keeps this call's in-memory
        // ArchivedAt byte-identical to what a later re-read from Postgres returns.
        var archivedAt = DateTimeOffset.UtcNow;
        device.ArchivedAt = archivedAt.AddTicks(-(archivedAt.Ticks % TimeSpan.TicksPerMicrosecond));
        await repository.UpdateDeviceAsync(device, cancellationToken);

        return device;
    }
}
