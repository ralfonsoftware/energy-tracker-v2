using EnergyTracker.Domain;

namespace EnergyTracker.Application.Ports;

public interface IEventRepository
{
    Task<Event> AddAsync(Event @event, CancellationToken cancellationToken);
}
