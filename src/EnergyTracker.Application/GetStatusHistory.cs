using EnergyTracker.Application.Ports;
using EnergyTracker.Domain;

namespace EnergyTracker.Application;

public record StatusHistoryEntry(Status Status, decimal PaceToDateKwh, decimal BaselineToDateKwh, bool IsLowConfidence, DateTimeOffset ComputedAtUtc, bool GapBeforeThisEntry);

/// <summary>Reads the caller's own Household's full StatusSnapshot lifetime for the Trend History chart (AC #4, #5, #6).</summary>
public class GetStatusHistory(
    IHouseholdRepository householdRepository,
    IStatusSnapshotRepository statusSnapshotRepository)
{
    public async Task<IReadOnlyList<StatusHistoryEntry>> ExecuteAsync(Guid householdId, CancellationToken cancellationToken)
    {
        var household = await householdRepository.FindByIdAsync(householdId, cancellationToken);
        if (household is null)
        {
            return [];
        }

        var snapshots = await statusSnapshotRepository.GetForHouseholdAsync(householdId, cancellationToken);

        var entries = new List<StatusHistoryEntry>(snapshots.Count);
        for (var i = 0; i < snapshots.Count; i++)
        {
            var gapBeforeThisEntry = i > 0 &&
                (snapshots[i].ComputedAtUtc - snapshots[i - 1].ComputedAtUtc).TotalDays > household.LowConfidenceGapDays;

            entries.Add(new StatusHistoryEntry(
                Status: snapshots[i].Status,
                PaceToDateKwh: snapshots[i].PaceToDateKwh,
                BaselineToDateKwh: snapshots[i].BaselineToDateKwh,
                IsLowConfidence: snapshots[i].IsLowConfidence,
                ComputedAtUtc: snapshots[i].ComputedAtUtc,
                GapBeforeThisEntry: gapBeforeThisEntry));
        }

        return entries;
    }
}
