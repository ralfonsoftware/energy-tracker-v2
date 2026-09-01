namespace EnergyTracker.Application.Ports;

// AD-7: "exactly one application service" recomputes and snapshots Status. Its interface lives
// here (Application/Ports) even though AD-7's prose calls it an "application service" — writing a
// StatusSnapshot row requires EF Core, an Infrastructure concern under AD-1, so the concrete
// implementation lives in Infrastructure/Adapters like every other port in this codebase.
public interface IStatusRecomputeService
{
    Task RecomputeAsync(Guid householdId, CancellationToken cancellationToken);

    // Story 4.3, AC #3: after a Meter Reading correction, recomputes Status forward from
    // fromEffectiveAtUtc through every later existing StatusSnapshot point and appends a
    // superseding row for each (StatusSnapshot stays immutable/insert-only — "recomputed forward"
    // means superseding by append, never mutating a row in place). This is a second method on the
    // same single service/port, not a second snapshot writer — AD-7's "exactly one application
    // service" invariant is about one writer class, not one method.
    //
    // fromEffectiveAtUtc must be a wall-clock value (a MeterReading's own CreatedAtUtc, never its
    // ReadingTimestamp) — see EditMeterReading's call site for why.
    Task RecomputeForwardFromAsync(Guid householdId, DateTimeOffset fromEffectiveAtUtc, CancellationToken cancellationToken);
}
