using EnergyTracker.Application.Ports;
using Microsoft.EntityFrameworkCore;

namespace EnergyTracker.Infrastructure.Adapters;

public class SmartPlugReadingRepository(EnergyTrackerDbContext dbContext) : ISmartPlugReadingRepository
{
    public async Task<IReadOnlyList<SmartPlugReadingAggregate>> GetAggregatedByTagAsync(Guid householdId, CancellationToken cancellationToken) =>
        await dbContext.SmartPlugReadings
            // Explicit filter alongside AD-3's global query filter — same redundant-but-explicit
            // discipline StatusSnapshotRepository.GetForHouseholdAsync established.
            .Where(r => r.HouseholdId == householdId && r.PowerPointId != null)
            // PowerPointId included as a disambiguator (Story 4.2 code-review fix): without it, a
            // Power Point renamed to a name a different Power Point has since been renamed away
            // from would silently merge both entities' history under one tuple.
            .GroupBy(r => new { r.PowerPointId, r.RoomName, r.PowerPointName, r.DeviceName })
            .Select(g => new SmartPlugReadingAggregate(g.Key.PowerPointId!.Value, g.Key.RoomName, g.Key.PowerPointName, g.Key.DeviceName, g.Sum(r => r.KwhValue)))
            .ToListAsync(cancellationToken);
}
