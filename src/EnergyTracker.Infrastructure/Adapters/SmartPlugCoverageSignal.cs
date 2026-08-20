using EnergyTracker.Application.Ports;
using EnergyTracker.Domain;
using Microsoft.EntityFrameworkCore;

namespace EnergyTracker.Infrastructure.Adapters;

// AD-14: freely references SmartPlugReading/SmartPlugImportGap — this file sits outside
// PatternDetectiveDoesNotReferenceSmartPlugOrEventDataTests' guarded file list (only
// GetCurrentStatus.cs, which consumes ISmartPlugCoverageSignal, is guarded).
public class SmartPlugCoverageSignal(EnergyTrackerDbContext dbContext) : ISmartPlugCoverageSignal
{
    public async Task<bool> HasCoverageDuringAsync(Guid householdId, DateTimeOffset start, DateTimeOffset end, CancellationToken cancellationToken)
    {
        var hasMeasuredReading = await dbContext.SmartPlugReadings
            .Where(r => r.HouseholdId == householdId && r.IntervalStart <= end && r.IntervalEnd >= start)
            .AnyAsync(cancellationToken);
        if (hasMeasuredReading)
        {
            return true;
        }

        // A bounded Estimated gap still counts as coverage (Task 4: "real or bounded-estimated
        // coverage counts") — only a Missing-flagged (or FlaggedForReview) gap does not, since
        // neither represents anything actually measured or bounded.
        var startDate = DateOnly.FromDateTime(start.DateTime);
        var endDate = DateOnly.FromDateTime(end.DateTime);
        return await dbContext.SmartPlugImportGaps
            .Where(g => g.HouseholdId == householdId
                && g.Treatment == SmartPlugImportGapTreatment.Estimated
                && g.StartDate <= endDate && g.EndDate >= startDate)
            .AnyAsync(cancellationToken);
    }
}
