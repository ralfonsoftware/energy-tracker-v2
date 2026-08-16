using EnergyTracker.Application.Ports;
using EnergyTracker.Domain;
using Microsoft.EntityFrameworkCore;

namespace EnergyTracker.Infrastructure.Adapters;

public class MeterRegressionPromptRepository(EnergyTrackerDbContext dbContext) : IMeterRegressionPromptRepository
{
    public async Task<MeterRegressionPrompt> AddAsync(MeterRegressionPrompt prompt, CancellationToken cancellationToken)
    {
        await dbContext.MeterRegressionPrompts.AddAsync(prompt, cancellationToken);

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
            return prompt;
        }
        catch (DbUpdateException)
        {
            // Same race class as MeterReadingRepository.AddAsync's IdempotencyKey guard: two
            // concurrent requests can both reach the regression-detection step for the same
            // winning MeterReading (via its own idempotency-key race) and both attempt to insert
            // a prompt for it. The unique MeterReadingId index is the actual guarantee here.
            dbContext.Entry(prompt).State = EntityState.Detached;
            var winner = await dbContext.MeterRegressionPrompts.SingleOrDefaultAsync(p => p.MeterReadingId == prompt.MeterReadingId, cancellationToken);
            if (winner is null)
            {
                throw;
            }

            return winner;
        }
    }

    public Task<MeterRegressionPrompt?> GetOpenForHouseholdAsync(Guid householdId, CancellationToken cancellationToken) =>
        dbContext.MeterRegressionPrompts
            .Where(p => p.HouseholdId == householdId && p.ResolvedAtUtc == null)
            .Join(dbContext.MeterReadings, p => p.MeterReadingId, r => r.Id, (p, r) => new { Prompt = p, r.ReadingTimestamp })
            // ReadingTimestamp alone can tie (e.g. two backfilled readings entered with the same
            // timestamp) — break ties on the prompt's own Id so ordering is deterministic across calls
            // and GET /open never disagrees with the resolve-time open-check.
            .OrderBy(x => x.ReadingTimestamp)
            .ThenBy(x => x.Prompt.Id)
            .Select(x => x.Prompt)
            .FirstOrDefaultAsync(cancellationToken);

    public Task<MeterRegressionPrompt?> FindByIdAsync(Guid householdId, Guid promptId, CancellationToken cancellationToken) =>
        dbContext.MeterRegressionPrompts.SingleOrDefaultAsync(p => p.HouseholdId == householdId && p.Id == promptId, cancellationToken);

    public async Task<bool> ResolveAsync(MeterRegressionPrompt prompt, CancellationToken cancellationToken)
    {
        // Conditional UPDATE guards against a concurrent resolve of the same prompt (e.g. a double-tap
        // on "Confirm"): only the request that still finds ResolvedAtUtc IS NULL at write time wins.
        var rowsUpdated = await dbContext.MeterRegressionPrompts
            .Where(p => p.Id == prompt.Id && p.ResolvedAtUtc == null)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(p => p.Classification, prompt.Classification)
                    .SetProperty(p => p.DigitCapacityKwh, prompt.DigitCapacityKwh)
                    .SetProperty(p => p.ResolvedAtUtc, prompt.ResolvedAtUtc),
                cancellationToken);

        return rowsUpdated > 0;
    }

    public Task<decimal?> GetMainMeterDigitCapacityAsync(Guid mainMeterId, CancellationToken cancellationToken) =>
        dbContext.MainMeters.Where(m => m.Id == mainMeterId).Select(m => m.DigitCapacityKwh).SingleOrDefaultAsync(cancellationToken);

    public async Task SetMainMeterDigitCapacityIfUnsetAsync(Guid mainMeterId, decimal digitCapacityKwh, CancellationToken cancellationToken)
    {
        await dbContext.MainMeters
            .Where(m => m.Id == mainMeterId && m.DigitCapacityKwh == null)
            .ExecuteUpdateAsync(setters => setters.SetProperty(m => m.DigitCapacityKwh, digitCapacityKwh), cancellationToken);
    }
}
