using EnergyTracker.Application.Ports;
using EnergyTracker.Domain;

namespace EnergyTracker.Application;

/// <summary>Creates a Device on a Power Point in the caller's own Household (AC #1, #4).</summary>
public class CreateDevice(ITaggingScaffoldRepository repository)
{
    public async Task<Device> ExecuteAsync(Guid householdId, Guid powerPointId, string name, CancellationToken cancellationToken)
    {
        var validatedName = TaggingScaffoldNameValidator.Validate(name);

        var siblings = await repository.ListDevicesAsync(cancellationToken);
        if (siblings.Any(d => d.PowerPointId == powerPointId && string.Equals(d.Name, validatedName, StringComparison.Ordinal)))
        {
            throw new TaggingScaffoldValidationException($"A Device named '{validatedName}' already exists on this Power Point.");
        }

        var powerPoint = await repository.FindPowerPointAsync(powerPointId, cancellationToken)
            ?? throw new TaggingScaffoldNotFoundException("PowerPoint", powerPointId);

        if (powerPoint.ArchivedAt is not null)
        {
            throw new TaggingScaffoldParentArchivedException("PowerPoint", powerPointId);
        }

        var device = new Device
        {
            Id = Guid.NewGuid(),
            HouseholdId = householdId,
            PowerPointId = powerPointId,
            Name = validatedName,
            CreatedAtUtc = DateTimeOffset.UtcNow,
            ArchivedAt = null,
        };

        await repository.AddDeviceAsync(device, cancellationToken);

        return device;
    }
}
