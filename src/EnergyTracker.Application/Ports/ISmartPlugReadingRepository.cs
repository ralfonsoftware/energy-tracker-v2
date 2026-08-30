namespace EnergyTracker.Application.Ports;

// Flat, pre-aggregated grouping result — one row per distinct (PowerPointId, RoomName,
// PowerPointName, DeviceName) tuple. Grouped on the snapshotted-by-value string columns, never a
// live join to Room/PowerPoint/Device, so a later retag can never rewrite this history (AD-10).
// PowerPointId is included purely as a disambiguator (it's already a stored column on the
// reading, not a join) so two distinct Power Points can never collapse into one tree node just
// because one of them was later renamed to a name the other has since been renamed away from —
// still no Room-level equivalent guard, since SmartPlugReading has no RoomId column to key on.
public record SmartPlugReadingAggregate(Guid PowerPointId, string RoomName, string PowerPointName, string DeviceName, decimal TotalKwh);

public interface ISmartPlugReadingRepository
{
    // Display-only read, deliberately separate from ISmartPlugImportRepository (which owns the
    // import pipeline's write path + its own gap-detection reads) — same split
    // IStatusSnapshotRepository drew from the write side in Story 4.1.
    Task<IReadOnlyList<SmartPlugReadingAggregate>> GetAggregatedByTagAsync(Guid householdId, CancellationToken cancellationToken);
}
