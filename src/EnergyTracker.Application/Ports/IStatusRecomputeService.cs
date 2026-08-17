namespace EnergyTracker.Application.Ports;

// AD-7: "exactly one application service" recomputes and snapshots Status. Its interface lives
// here (Application/Ports) even though AD-7's prose calls it an "application service" — writing a
// StatusSnapshot row requires EF Core, an Infrastructure concern under AD-1, so the concrete
// implementation lives in Infrastructure/Adapters like every other port in this codebase.
public interface IStatusRecomputeService
{
    Task RecomputeAsync(Guid householdId, CancellationToken cancellationToken);
}
