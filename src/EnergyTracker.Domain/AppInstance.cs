namespace EnergyTracker.Domain;

// One row per process start: instance-level operational data, not Household data (AD-24, an AD-3
// exemption like DataProtectionKeys), so there is no HouseholdId and no query filter. Its only
// reader is the overlap warning in the log; nothing decides anything from it (AD-7).
public class AppInstance
{
    public required Guid Id { get; init; }

    public required DateTimeOffset StartedAtUtc { get; init; }

    public required DateTimeOffset LastSeenAtUtc { get; set; }
}
