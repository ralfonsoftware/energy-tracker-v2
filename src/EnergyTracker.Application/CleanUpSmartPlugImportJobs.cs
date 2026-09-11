using EnergyTracker.Application.Ports;

namespace EnergyTracker.Application;

/// <summary>Manually deletes Smart Plug import job/audit records, across all six states, on the household's request (Story 3.10).</summary>
public class CleanUpSmartPlugImportJobs(ISmartPlugImportRepository smartPlugImportRepository)
{
    public async Task<int> ExecuteAsync(Guid householdId, bool deleteAll, CancellationToken cancellationToken)
    {
        // Shares ListSmartPlugImportJobs' RetentionWindow rather than an independently-declared
        // duplicate, so the "older than 30 days" manual mode never silently drifts from the
        // automatic sweep's own window.
        DateTimeOffset? cutoffUtc = deleteAll ? null : DateTimeOffset.UtcNow - ListSmartPlugImportJobs.RetentionWindow;
        return await smartPlugImportRepository.DeleteJobsAsync(householdId, cutoffUtc, cancellationToken);
    }
}
