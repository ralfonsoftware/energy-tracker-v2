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
}
