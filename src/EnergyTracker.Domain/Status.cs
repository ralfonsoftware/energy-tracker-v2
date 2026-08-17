namespace EnergyTracker.Domain;

// Exactly 3 members — mirrors the UX rule that there is no 4th visual status state. "Undefined"
// (fewer than two Meter Readings, or no Yearly Baseline set) is represented by a null Status
// result from the service/API layer, never a 4th enum case.
public enum Status
{
    WithinRange,
    BelowBaseline,
    Trending,
}
