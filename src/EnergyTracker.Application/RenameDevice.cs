using EnergyTracker.Application.Ports;
using EnergyTracker.Domain;

namespace EnergyTracker.Application;

/// <summary>Renames a Device (AC #2). Renaming an archived Device is allowed.</summary>
public class RenameDevice(ITaggingScaffoldRepository repository)
{
    public async Task<Device> ExecuteAsync(Guid deviceId, string name, CancellationToken cancellationToken)
    {
        var device = await repository.FindDeviceAsync(deviceId, cancellationToken)
            ?? throw new TaggingScaffoldNotFoundException("Device", deviceId);

        var validatedName = TaggingScaffoldNameValidator.Validate(name);

        var siblings = await repository.ListDevicesAsync(cancellationToken);
        if (siblings.Any(d => d.Id != deviceId && d.PowerPointId == device.PowerPointId && string.Equals(d.Name, validatedName, StringComparison.Ordinal)))
        {
            throw new TaggingScaffoldValidationException($"A Device named '{validatedName}' already exists on this Power Point.");
        }

        device.Name = validatedName;

        await repository.UpdateDeviceAsync(device, cancellationToken);

        return device;
    }
}
