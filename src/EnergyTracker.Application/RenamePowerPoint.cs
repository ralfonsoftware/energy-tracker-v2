using EnergyTracker.Application.Ports;
using EnergyTracker.Domain;

namespace EnergyTracker.Application;

/// <summary>Renames a Power Point (AC #2). Renaming an archived Power Point is allowed.</summary>
public class RenamePowerPoint(ITaggingScaffoldRepository repository)
{
    public async Task<PowerPoint> ExecuteAsync(Guid powerPointId, string name, CancellationToken cancellationToken)
    {
        var powerPoint = await repository.FindPowerPointAsync(powerPointId, cancellationToken)
            ?? throw new TaggingScaffoldNotFoundException("PowerPoint", powerPointId);

        var validatedName = TaggingScaffoldNameValidator.Validate(name);

        var siblings = await repository.ListPowerPointsAsync(cancellationToken);
        if (siblings.Any(p => p.Id != powerPointId && p.RoomId == powerPoint.RoomId && string.Equals(p.Name, validatedName, StringComparison.Ordinal)))
        {
            throw new TaggingScaffoldValidationException($"A Power Point named '{validatedName}' already exists in this Room.");
        }

        powerPoint.Name = validatedName;

        await repository.UpdatePowerPointAsync(powerPoint, cancellationToken);

        return powerPoint;
    }
}
