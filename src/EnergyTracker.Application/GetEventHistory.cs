using EnergyTracker.Application.Ports;
using EnergyTracker.Domain;

namespace EnergyTracker.Application;

public record EventHistoryPage(IReadOnlyList<Event> Items, int TotalCount, int Page, int PageSize);

/// <summary>Reads a paginated, reverse-chronological page of the caller's own Household's Events (AC #1).</summary>
public class GetEventHistory(IEventRepository repository)
{
    private const int MaxPageSize = 100;

    public async Task<EventHistoryPage> ExecuteAsync(int page, int pageSize, CancellationToken cancellationToken)
    {
        if (page < 1)
        {
            throw new EventValidationException($"page must be at least 1, got '{page}'.", "event.page_invalid");
        }

        if (pageSize < 1 || pageSize > MaxPageSize)
        {
            throw new EventValidationException($"pageSize must be between 1 and {MaxPageSize}, got '{pageSize}'.", "event.page_size_invalid");
        }

        // Guards the repository's Skip((page - 1) * pageSize) against an int32 overflow for an
        // absurdly large page — checked in long arithmetic so the check itself can't overflow.
        if ((long)(page - 1) * pageSize > int.MaxValue)
        {
            throw new EventValidationException($"page {page} is out of range for pageSize {pageSize}.", "event.page_invalid");
        }

        var (items, totalCount) = await repository.GetPageForHouseholdAsync(page, pageSize, cancellationToken);

        return new EventHistoryPage(items, totalCount, page, pageSize);
    }
}
