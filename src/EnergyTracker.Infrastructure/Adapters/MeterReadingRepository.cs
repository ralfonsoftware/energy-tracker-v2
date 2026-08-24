using EnergyTracker.Application;
using EnergyTracker.Application.Ports;
using EnergyTracker.Domain;
using Microsoft.EntityFrameworkCore;

namespace EnergyTracker.Infrastructure.Adapters;

public class MeterReadingRepository(EnergyTrackerDbContext dbContext) : IMeterReadingRepository
{
    public Task<MeterReading?> FindByIdempotencyKeyAsync(Guid idempotencyKey, CancellationToken cancellationToken) =>
        dbContext.MeterReadings.SingleOrDefaultAsync(r => r.IdempotencyKey == idempotencyKey, cancellationToken);

    public async Task<MainMeter> GetOrCreateMainMeterAsync(Guid householdId, CancellationToken cancellationToken)
    {
        var existing = await dbContext.MainMeters.SingleOrDefaultAsync(m => m.HouseholdId == householdId, cancellationToken);
        if (existing is not null)
        {
            return existing;
        }

        var mainMeter = new MainMeter
        {
            Id = Guid.NewGuid(),
            HouseholdId = householdId,
            CreatedAtUtc = DateTimeOffset.UtcNow,
        };

        await dbContext.MainMeters.AddAsync(mainMeter, cancellationToken);

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
            return mainMeter;
        }
        catch (DbUpdateException)
        {
            // Two concurrent "first ever reading" calls for the same Household raced on the
            // unique HouseholdId index — the other request's row already won. Detach the failed
            // insert and re-query for the winner rather than letting this surface as a 500.
            dbContext.Entry(mainMeter).State = EntityState.Detached;
            var winner = await dbContext.MainMeters.SingleOrDefaultAsync(m => m.HouseholdId == householdId, cancellationToken);
            if (winner is null)
            {
                throw;
            }

            return winner;
        }
    }

    public async Task<MeterReading> AddAsync(MeterReading reading, CancellationToken cancellationToken)
    {
        await dbContext.MeterReadings.AddAsync(reading, cancellationToken);

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
            return reading;
        }
        catch (DbUpdateException)
        {
            // A concurrent retry of the same logical reading (same IdempotencyKey) beat this one
            // to the insert — the unique IdempotencyKey index is AD-16's actual guarantee, this
            // upfront-check-then-act path is just the fast path. Treat it as the no-op it is
            // rather than letting the constraint violation surface as a 500 — and hand the caller
            // back the row that actually won, not this request's never-persisted local instance.
            dbContext.Entry(reading).State = EntityState.Detached;
            var winner = await dbContext.MeterReadings.SingleOrDefaultAsync(r => r.IdempotencyKey == reading.IdempotencyKey, cancellationToken);
            if (winner is null)
            {
                throw;
            }

            return winner;
        }
    }

    public Task<MeterReading?> FindImmediatelyPrecedingAsync(Guid mainMeterId, DateTimeOffset readingTimestamp, CancellationToken cancellationToken) =>
        dbContext.MeterReadings
            .Where(r => r.MainMeterId == mainMeterId && r.ReadingTimestamp < readingTimestamp)
            // ReadingTimestamp alone can tie (e.g. two backfilled readings entered with the same
            // timestamp) — break ties on Id so "immediately preceding" is deterministic across calls.
            .OrderByDescending(r => r.ReadingTimestamp)
            .ThenByDescending(r => r.Id)
            .FirstOrDefaultAsync(cancellationToken);

    public Task<MeterReading?> FindByIdAsync(Guid readingId, CancellationToken cancellationToken) =>
        dbContext.MeterReadings.SingleOrDefaultAsync(r => r.Id == readingId, cancellationToken);

    public Task<MainMeter?> FindMainMeterByHouseholdAsync(Guid householdId, CancellationToken cancellationToken) =>
        dbContext.MainMeters.SingleOrDefaultAsync(m => m.HouseholdId == householdId, cancellationToken);

    public async Task<IReadOnlyList<MeterReading>> GetRecentByMainMeterAsync(
        Guid mainMeterId, int windowDays, Guid? mustIncludeReadingId, CancellationToken cancellationToken)
    {
        var latest = await dbContext.MeterReadings
            .Where(r => r.MainMeterId == mainMeterId)
            .OrderByDescending(r => r.ReadingTimestamp)
            .Select(r => new { r.ReadingTimestamp })
            .FirstOrDefaultAsync(cancellationToken);

        if (latest is null)
        {
            // No readings at all for this Main Meter.
            return [];
        }

        var latestTimestamp = latest.ReadingTimestamp;

        // The must-include lookup is filtered on MainMeterId too, not just Id — defensive, matches
        // ExcludeFromOpenPrompt's own defensiveness a few lines away in PatternDetectiveCalculator.
        // Id alone is already PK-filtered (at most one row); this never returns more than one.
        DateTimeOffset? mustIncludeTimestamp = null;
        if (mustIncludeReadingId is { } mustIncludeId)
        {
            var mustInclude = await dbContext.MeterReadings
                .Where(r => r.Id == mustIncludeId && r.MainMeterId == mainMeterId)
                .Select(r => new { r.ReadingTimestamp })
                .FirstOrDefaultAsync(cancellationToken);
            mustIncludeTimestamp = mustInclude?.ReadingTimestamp;
        }

        // windowDays is subtracted AFTER taking the min of the two anchors — applying it to only
        // one side (or dropping it for the must-include branch) would fetch just the anchor
        // reading itself instead of a trailing window behind it.
        var anchor = mustIncludeTimestamp is { } mustInclude2 && mustInclude2 < latestTimestamp
            ? mustInclude2
            : latestTimestamp;
        var cutoff = anchor - TimeSpan.FromDays(windowDays);

        return await dbContext.MeterReadings
            .Where(r => r.MainMeterId == mainMeterId && r.ReadingTimestamp >= cutoff)
            .OrderBy(r => r.ReadingTimestamp)
            .ThenBy(r => r.Id)
            .ToListAsync(cancellationToken);
    }

    public async Task<(IReadOnlyList<MeterReading> Items, int TotalCount)> GetPageForMainMeterAsync(Guid mainMeterId, int page, int pageSize, CancellationToken cancellationToken)
    {
        var query = dbContext.MeterReadings.Where(r => r.MainMeterId == mainMeterId);

        var totalCount = await query.CountAsync(cancellationToken);
        var items = await query
            .OrderByDescending(r => r.ReadingTimestamp)
            .ThenByDescending(r => r.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);

        return (items, totalCount);
    }

    public async Task<MeterReading> UpdateKwhValueAsync(Guid readingId, decimal kwhValue, int expectedVersion, CancellationToken cancellationToken)
    {
        var reading = await dbContext.MeterReadings.SingleAsync(r => r.Id == readingId, cancellationToken);

        // Makes EF's SaveChangesAsync compare expectedVersion (the caller's known value) against
        // the DB, not whatever the freshly-loaded entity already has.
        dbContext.Entry(reading).Property(r => r.Version).OriginalValue = expectedVersion;

        reading.KwhValue = kwhValue;
        // AD-4 requires the concurrency token to change on every update — same reasoning as
        // HouseholdRepository.UpdateYearlyBaselineAsync's household.Version++.
        reading.Version++;

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new MeterReadingConcurrencyConflictException(readingId);
        }

        return reading;
    }
}
