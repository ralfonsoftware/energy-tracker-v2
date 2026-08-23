using EnergyTracker.Application.Ports;

namespace EnergyTracker.Infrastructure.Adapters;

public class UnitOfWork(EnergyTrackerDbContext dbContext) : IUnitOfWork
{
    public async Task<TResult> ExecuteInTransactionAsync<TResult>(Func<CancellationToken, Task<TResult>> operation, CancellationToken cancellationToken)
    {
        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        var result = await operation(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return result;
    }
}
