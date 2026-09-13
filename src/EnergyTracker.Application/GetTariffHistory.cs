using EnergyTracker.Application.Ports;
using EnergyTracker.Domain;

namespace EnergyTracker.Application;

public record TariffHistoryPage(IReadOnlyList<TariffHistoryEntry> Items, int TotalCount, int Page, int PageSize);

// Corrections is keyed by FieldName (e.g. "MonthlyBaseFee", "PricePerKwh") — a Tariff entry can
// have up to five independently-corrected fields (Task 4), unlike MeterReading's single
// LatestCorrection.
public record TariffHistoryEntry(Tariff Tariff, bool IsCurrent, DateTimeOffset? EffectiveUntil, IReadOnlyDictionary<string, AuditCorrection> Corrections);

/// <summary>Reads a paginated, timestamp-ordered page of the caller's own Household's Tariff history, enriched with current/effective-until and per-field correction data (AC #1, #2, #6).</summary>
public class GetTariffHistory(ITariffRepository tariffRepository, IAuditCorrectionRecorder auditCorrectionRecorder)
{
    private const int MaxPageSize = 100;

    public async Task<TariffHistoryPage> ExecuteAsync(Guid householdId, int page, int pageSize, CancellationToken cancellationToken)
    {
        if (page < 1)
        {
            throw new TariffValidationException($"page must be at least 1, got '{page}'.");
        }

        if (pageSize < 1 || pageSize > MaxPageSize)
        {
            throw new TariffValidationException($"pageSize must be between 1 and {MaxPageSize}, got '{pageSize}'.");
        }

        // Guards GetHistoryForHouseholdAsync's Skip((page - 1) * pageSize) against an int32
        // overflow for an absurdly large page — checked in long arithmetic so the check itself
        // can't overflow (mirrors GetMeterReadingHistory's own guard).
        if ((long)(page - 1) * pageSize > int.MaxValue)
        {
            throw new TariffValidationException($"page {page} is out of range for pageSize {pageSize}.");
        }

        var (items, totalCount) = await tariffRepository.GetHistoryForHouseholdAsync(householdId, page, pageSize, cancellationToken);

        // IsCurrent/EffectiveUntil are computed across the WHOLE household's history (by
        // ContractStartDate ascending), not just within the returned page, so a paginated view
        // never mislabels an entry — the "next-later" entry may live on a different page. A
        // Tariff's history is a small, slow-growing set (a handful of entries per household over
        // years, never MeterReading's volume) so a second unpaged fetch when the page doesn't
        // already cover everything is proportionate.
        var fullHistory = totalCount <= items.Count
            ? items
            : (await tariffRepository.GetHistoryForHouseholdAsync(householdId, 1, totalCount, cancellationToken)).Items;
        var orderedAscending = fullHistory.OrderBy(t => t.ContractStartDate).ThenBy(t => t.Id).ToList();

        var now = DateTimeOffset.UtcNow;
        var currentId = orderedAscending.LastOrDefault(t => t.ContractStartDate <= now)?.Id;

        var effectiveUntilById = new Dictionary<Guid, DateTimeOffset?>();
        for (var i = 0; i < orderedAscending.Count; i++)
        {
            effectiveUntilById[orderedAscending[i].Id] = i + 1 < orderedAscending.Count
                ? orderedAscending[i + 1].ContractStartDate
                : null;
        }

        // One batch call for the whole page, not N+1.
        var corrections = await auditCorrectionRecorder.GetLatestPerFieldForEntitiesAsync(
            "Tariff", items.Select(t => t.Id).ToList(), cancellationToken);

        var entries = items
            .Select(tariff => new TariffHistoryEntry(
                tariff,
                tariff.Id == currentId,
                effectiveUntilById.GetValueOrDefault(tariff.Id),
                corrections
                    .Where(kv => kv.Key.EntityId == tariff.Id)
                    .ToDictionary(kv => kv.Key.FieldName, kv => kv.Value)))
            .ToList();

        return new TariffHistoryPage(entries, totalCount, page, pageSize);
    }
}
