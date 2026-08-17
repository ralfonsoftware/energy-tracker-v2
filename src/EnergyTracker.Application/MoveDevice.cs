using EnergyTracker.Application.Ports;
using EnergyTracker.Domain;

namespace EnergyTracker.Application;

/// <summary>Moves a Device to a different Power Point in the caller's own Household (AC #2, #4, #5, #6).</summary>
public class MoveDevice(ITaggingScaffoldRepository repository)
{
    public async Task<Device> ExecuteAsync(Guid deviceId, Guid newPowerPointId, CancellationToken cancellationToken)
    {
        var device = await repository.FindDeviceAsync(deviceId, cancellationToken)
            ?? throw new TaggingScaffoldNotFoundException("Device", deviceId);

        var siblings = await repository.ListDevicesAsync(cancellationToken);
        if (siblings.Any(d => d.Id != deviceId && d.PowerPointId == newPowerPointId
            && string.Equals(d.Name, device.Name, StringComparison.Ordinal)))
        {
            throw new TaggingScaffoldValidationException($"A Device named '{device.Name}' already exists on this Power Point.");
        }

        var newPowerPoint = await repository.FindPowerPointAsync(newPowerPointId, cancellationToken)
            ?? throw new TaggingScaffoldNotFoundException("PowerPoint", newPowerPointId);

        if (newPowerPoint.ArchivedAt is not null)
        {
            throw new TaggingScaffoldParentArchivedException("PowerPoint", newPowerPointId);
        }

        device.PowerPointId = newPowerPointId;
        await repository.UpdateDeviceAsync(device, cancellationToken);

        return device;
    }
}
