using EnergyTracker.Domain;

namespace EnergyTracker.Application.Ports;

public interface IEventRepository
{
    Task<Event> AddAsync(Event @event, CancellationToken cancellationToken);

    // No householdId parameter — AD-3's DbContext query filter already scopes the read, exactly as
    // ITaggingScaffoldRepository.List*Async does.
    Task<(IReadOnlyList<Event> Items, int TotalCount)> GetPageForHouseholdAsync(
        int page, int pageSize, CancellationToken cancellationToken);
}
