using EnergyTracker.Application.Ports;
using EnergyTracker.Domain;

namespace EnergyTracker.Application;

/// <summary>Reads a Background Job's current status for the caller's own Household, the poll target AC #2's completion signal relies on (AC #2, #6).</summary>
public class GetBackgroundJobStatus(IBackgroundJobRepository repository, ISmartPlugImportRepository smartPlugImportRepository)
{
    public async Task<BackgroundJobStatusResult?> ExecuteAsync(Guid householdId, Guid jobId, CancellationToken cancellationToken)
    {
        var job = await repository.FindByIdAsync(householdId, jobId, cancellationToken);
        if (job is null)
        {
            return null;
        }

        // "Completed" alone doesn't tell the client whether the import fully attached to a Power
        // Point or is parked AwaitingPowerPointMapping — surface the import's own sub-status too.
        SmartPlugImportStatus? importStatus = null;
        if (job.JobType == JobTypes.ProcessSmartPlugImport)
        {
            var import = await smartPlugImportRepository.FindByBackgroundJobIdAsync(jobId, cancellationToken);
            importStatus = import?.Status;
        }

        return new BackgroundJobStatusResult(job, importStatus);
    }
}

public record BackgroundJobStatusResult(BackgroundJob Job, SmartPlugImportStatus? SmartPlugImportStatus);
