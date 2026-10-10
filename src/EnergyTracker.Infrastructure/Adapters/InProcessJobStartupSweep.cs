using EnergyTracker.Application.Ports;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace EnergyTracker.Infrastructure.Adapters;

// AD-24: process-local by design. The in-process Channel queue loses every pending message when
// the app restarts, so any Queued/Processing row created before this process started can never be
// picked up again — failing it as job-interrupted (AD-6, amended 2026-10-10) frees the Household
// to retry instead of staying blocked forever. Registered for the in-process adapter only; the
// Azure Storage Queue adapter never runs this sweep (its messages survive a restart).
// It relies on AD-24's single-instance reading: exactly one in-process instance owns the database.
// A second instance sharing it would have its live jobs failed here, which is why running more than
// one in-process instance against one database is unsupported (docs/self-hosting.md).
public class InProcessJobStartupSweep(IServiceScopeFactory scopeFactory, ILogger<InProcessJobStartupSweep> logger)
{
    public async Task SweepAsync(DateTimeOffset processStartedAtUtc, CancellationToken cancellationToken)
    {
        // Household has no query filter (AD-3-exempt), so listing ids needs no household context.
        List<Guid> householdIds;
        using (var listScope = scopeFactory.CreateScope())
        {
            householdIds = await listScope.ServiceProvider.GetRequiredService<EnergyTrackerDbContext>().Households
                .AsNoTracking()
                .Select(h => h.Id)
                .ToListAsync(cancellationToken);
        }

        var failedTotal = 0;
        foreach (var householdId in householdIds)
        {
            try
            {
                // A fresh scope per Household, JobHouseholdContext set before anything resolves
                // EnergyTrackerDbContext: CurrentHouseholdAccessor caches the first resolution for
                // the scope's life, so reusing one scope would apply the first Household's query
                // filter to everyone.
                using var scope = scopeFactory.CreateScope();
                scope.ServiceProvider.GetRequiredService<JobHouseholdContext>().HouseholdId = householdId;
                var lifecycle = scope.ServiceProvider.GetRequiredService<IBackgroundJobLifecycle>();
                failedTotal += await lifecycle.FailInterruptedBeforeAsync(householdId, processStartedAtUtc, cancellationToken);
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                logger.LogError(ex, "Startup job sweep failed for Household {HouseholdId}; continuing with the others.", householdId);
            }
        }

        logger.LogInformation(
            "Startup job sweep failed {Count} job(s) left over from before this process started (job-interrupted).", failedTotal);
    }
}
