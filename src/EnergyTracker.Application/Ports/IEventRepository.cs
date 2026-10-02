using EnergyTracker.Domain;

namespace EnergyTracker.Application.Ports;

public interface IEventRepository
{
    Task<Event> AddAsync(Event @event, CancellationToken cancellationToken);

    // No householdId parameter — AD-3's DbContext query filter already scopes the read, exactly as
    // ITaggingScaffoldRepository.List*Async does.
    Task<(IReadOnlyList<Event> Items, int TotalCount)> GetPageForHouseholdAsync(
        int page, int pageSize, CancellationToken cancellationToken);

    // Story 10.2 — both rely on AD-3's DbContext query filter, like GetPageForHouseholdAsync.
    Task<Event?> FindByIdAsync(Guid eventId, CancellationToken cancellationToken);

    // Events whose OccurredAt lies in [from, to], inclusive at both ends, ordered by OccurredAt then Id.
    Task<IReadOnlyList<Event>> GetByOccurredAtRangeAsync(
        DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken);

    // Story 6.3 / 10.2 — CorrelateEvent's write; latest evaluation wins. `direction` and
    // `computedAtUtc` are both null (no correlation) or both set ("Bump"|"Dip"), never
    // independently. A no-op if the Event no longer exists (there is no Event deletion path in this
    // codebase, but the job runs asynchronously after creation, so this stays defensive rather than
    // assuming the row is always still there).
    Task SetCorrelationAsync(Guid eventId, string? direction, DateTimeOffset? computedAtUtc, CancellationToken cancellationToken);
}
