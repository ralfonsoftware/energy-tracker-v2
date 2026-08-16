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

    public async Task<MeterRegressionPrompt?> GetOpenForHouseholdAsync(Guid householdId, CancellationToken cancellationToken)
    {
        var openPrompts = await dbContext.MeterRegressionPrompts
            .Where(p => p.HouseholdId == householdId && p.ResolvedAtUtc == null)
            .Join(dbContext.MeterReadings, p => p.MeterReadingId, r => r.Id, (p, r) => new { Prompt = p, r.ReadingTimestamp })
            .OrderBy(x => x.ReadingTimestamp)
            .Select(x => x.Prompt)
            .Take(1)
            .ToListAsync(cancellationToken);

        return openPrompts.SingleOrDefault();
    }

    public Task<MeterRegressionPrompt?> FindByIdAsync(Guid householdId, Guid promptId, CancellationToken cancellationToken) =>
        dbContext.MeterRegressionPrompts.SingleOrDefaultAsync(p => p.HouseholdId == householdId && p.Id == promptId, cancellationToken);

    public async Task<MeterRegressionPrompt> ResolveAsync(MeterRegressionPrompt prompt, CancellationToken cancellationToken)
    {
        dbContext.MeterRegressionPrompts.Update(prompt);
        await dbContext.SaveChangesAsync(cancellationToken);
        return prompt;
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
