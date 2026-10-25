namespace EnergyTracker.Application;

/// <summary>An active job of a type that is exclusive per Household already exists, so the database refused a second one (AD-6, Story 11.14, audit C10); carries only the job type and the existing job's id.</summary>
public class ActiveJobAlreadyExistsException(string jobType, Guid activeJobId)
    : Exception($"A {jobType} job is already active for this Household (job {activeJobId}).")
{
    public string JobType { get; } = jobType;

    public Guid ActiveJobId { get; } = activeJobId;
}
