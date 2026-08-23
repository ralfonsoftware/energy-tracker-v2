using EnergyTracker.Application.Ports;
using EnergyTracker.Domain;

namespace EnergyTracker.Application;

public record MeterReadingHistoryPage(IReadOnlyList<MeterReadingHistoryEntry> Items, int TotalCount, int Page, int PageSize);

public record MeterReadingHistoryEntry(MeterReading Reading, bool IsPendingRegression, AuditCorrection? LatestCorrection);

/// <summary>Reads a paginated, timestamp-ordered page of the caller's own Household's Meter Readings, enriched with pending-regression and correction-note data (AC #1, #4, #5).</summary>
public class GetMeterReadingHistory(
    IMeterReadingRepository readingRepository,
    IMeterRegressionPromptRepository regressionPromptRepository,
    IAuditCorrectionRecorder auditCorrectionRecorder)
{
    private const int MaxPageSize = 100;

    public async Task<MeterReadingHistoryPage> ExecuteAsync(Guid householdId, int page, int pageSize, CancellationToken cancellationToken)
    {
        if (page < 1)
        {
            throw new MeterReadingValidationException($"page must be at least 1, got '{page}'.");
        }

        if (pageSize < 1 || pageSize > MaxPageSize)
        {
            throw new MeterReadingValidationException($"pageSize must be between 1 and {MaxPageSize}, got '{pageSize}'.");
        }

        // Guards GetPageForMainMeterAsync's Skip((page - 1) * pageSize) against an int32 overflow
        // for an absurdly large page — checked in long arithmetic so the check itself can't overflow.
        if ((long)(page - 1) * pageSize > int.MaxValue)
        {
            throw new MeterReadingValidationException($"page {page} is out of range for pageSize {pageSize}.");
        }

        // Read-only lookup, not GetOrCreateMainMeterAsync — viewing history must never have the
        // side effect of creating a Main Meter for a Household that has never logged a reading.
        var mainMeter = await readingRepository.FindMainMeterByHouseholdAsync(householdId, cancellationToken);
        if (mainMeter is null)
        {
            return new MeterReadingHistoryPage([], 0, page, pageSize);
        }

        var (items, totalCount) = await readingRepository.GetPageForMainMeterAsync(mainMeter.Id, page, pageSize, cancellationToken);

        // AD-12 guarantees at most one open prompt per Main Meter, so "pending" is just an
        // equality check per item — no per-row query needed.
        var openPrompt = await regressionPromptRepository.GetOpenForHouseholdAsync(householdId, cancellationToken);

        // One batch call for the whole page, not N+1.
        var corrections = await auditCorrectionRecorder.GetLatestForEntitiesAsync(
            "MeterReading", items.Select(r => r.Id).ToList(), cancellationToken);

        var entries = items
            .Select(reading => new MeterReadingHistoryEntry(
                reading,
                reading.Id == openPrompt?.MeterReadingId,
                corrections.GetValueOrDefault(reading.Id)))
            .ToList();

        return new MeterReadingHistoryPage(entries, totalCount, page, pageSize);
    }
}
