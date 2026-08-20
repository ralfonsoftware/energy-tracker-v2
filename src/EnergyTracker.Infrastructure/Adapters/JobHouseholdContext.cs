namespace EnergyTracker.Infrastructure.Adapters;

// AD-3's second ICurrentHouseholdAccessor resolution path — there is no HTTP principal to resolve
// from while processing a dequeued job envelope. Scoped: the job-processing loop creates a fresh
// DI scope per dequeued envelope and sets HouseholdId on it before resolving/invoking the use
// case, so every downstream query filter sees the right Household with no
// IgnoreQueryFilters()/raw-lookup workaround.
public class JobHouseholdContext
{
    public Guid? HouseholdId { get; set; }
}
