using EnergyTracker.Application.Ports;

namespace EnergyTracker.Application;

public record DeviceMeasuredData(string DeviceName, decimal TotalKwh);

public record PowerPointMeasuredData(string PowerPointName, decimal TotalKwh, IReadOnlyList<DeviceMeasuredData> Devices);

public record RoomMeasuredData(string RoomName, decimal TotalKwh, IReadOnlyList<PowerPointMeasuredData> PowerPoints);

/// <summary>Builds the Room -&gt; Power Point -&gt; Device measured-data tree for the Per-Plug view (AC #1).</summary>
public class GetPerPlugMeasuredData(ISmartPlugReadingRepository smartPlugReadingRepository)
{
    public async Task<IReadOnlyList<RoomMeasuredData>> ExecuteAsync(Guid householdId, CancellationToken cancellationToken)
    {
        var aggregates = await smartPlugReadingRepository.GetAggregatedByTagAsync(householdId, cancellationToken);

        // Ordering isn't specified by the epic/mockups — alphabetical ascending at every level is
        // the deterministic default (non-blocking assumption, per Story 4.1's precedent).
        // InvariantCulture, not Ordinal (code-review fix): these are user-entered free-text names
        // and this product ships full de-DE localization — ordinal (raw codepoint) ordering puts
        // ä/ö/ü after 'z' instead of next to their base letter.
        return aggregates
            .GroupBy(a => a.RoomName)
            .OrderBy(roomGroup => roomGroup.Key, StringComparer.InvariantCulture)
            .Select(roomGroup =>
            {
                var powerPoints = roomGroup
                    // Keyed on (PowerPointId, PowerPointName), not PowerPointName alone: the same
                    // Power Point can legitimately appear under two different snapshotted names
                    // across a rename (AD-10 keeps each name's history separate) — that's kept
                    // intact — while two *different* Power Points that happen to share a name
                    // string (one renamed away from it, another later renamed/created into it)
                    // must never collapse into one node (Story 4.2 code-review fix).
                    .GroupBy(a => new { a.PowerPointId, a.PowerPointName })
                    .OrderBy(ppGroup => ppGroup.Key.PowerPointName, StringComparer.InvariantCulture)
                    .Select(ppGroup =>
                    {
                        var devices = ppGroup
                            .OrderBy(a => a.DeviceName, StringComparer.InvariantCulture)
                            .Select(a => new DeviceMeasuredData(a.DeviceName, a.TotalKwh))
                            .ToList();

                        return new PowerPointMeasuredData(ppGroup.Key.PowerPointName, devices.Sum(d => d.TotalKwh), devices);
                    })
                    .ToList();

                return new RoomMeasuredData(roomGroup.Key, powerPoints.Sum(pp => pp.TotalKwh), powerPoints);
            })
            .ToList();
    }
}
