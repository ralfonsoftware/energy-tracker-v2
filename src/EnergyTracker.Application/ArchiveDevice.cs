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

        device.ArchivedAt = DateTimeOffset.UtcNow;
        await repository.UpdateDeviceAsync(device, cancellationToken);

        return device;
    }
}
