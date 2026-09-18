using EnergyTracker.Application.Ports;
using EnergyTracker.Domain;

namespace EnergyTracker.Infrastructure.Adapters;

public class EventRepository(EnergyTrackerDbContext dbContext) : IEventRepository
{
    public async Task<Event> AddAsync(Event @event, CancellationToken cancellationToken)
    {
        await dbContext.Events.AddAsync(@event, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);

        // UtcDateTimeOffsetConverter only translates the CLR<->provider boundary during the actual
        // write; it never rewrites this already-tracked instance's OccurredAt back to the persisted
        // UTC value. Without this reload, the caller (and the API response built from it) would
        // still see the client's original, non-normalized offset. Reload's own query goes through
        // AD-3's HasQueryFilter like any other query, but that can never exclude this row: @event's
        // HouseholdId is always the caller's own current household (set just above by the caller),
        // which is exactly what the filter matches against.
        await dbContext.Entry(@event).ReloadAsync(cancellationToken);
        return @event;
    }
}
