using EnergyTracker.Application.Ports;
using EnergyTracker.Domain;

namespace EnergyTracker.Infrastructure.Adapters;

public class EventRepository(EnergyTrackerDbContext dbContext) : IEventRepository
{
    public async Task<Event> AddAsync(Event @event, CancellationToken cancellationToken)
    {
        await dbContext.Events.AddAsync(@event, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
        return @event;
    }
}
