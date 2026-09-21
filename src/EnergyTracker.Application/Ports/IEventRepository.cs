using EnergyTracker.Domain;

namespace EnergyTracker.Application.Ports;

public interface IEventRepository
{
    Task<Event> AddAsync(Event @event, CancellationToken cancellationToken);

    // No householdId parameter — AD-3's DbContext query filter already scopes the read, exactly as
    // ITaggingScaffoldRepository.List*Async does.
    Task<(IReadOnlyList<Event> Items, int TotalCount)> GetPageForHouseholdAsync(
        int page, int pageSize, CancellationToken cancellationToken);

    // Story 6.3 — CorrelateEvent's terminal write. `direction` is "Bump"|"Dip"|null (both fields
    // always set together, never independently). A no-op if the Event no longer exists (there is
    // no Event deletion path in this codebase, but the job runs asynchronously after creation, so
    // this stays defensive rather than assuming the row is always still there).
    Task SetCorrelationAsync(Guid eventId, string? direction, DateTimeOffset computedAtUtc, CancellationToken cancellationToken);
}
