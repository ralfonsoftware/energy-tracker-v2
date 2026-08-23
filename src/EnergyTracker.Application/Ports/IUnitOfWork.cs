namespace EnergyTracker.Application.Ports;

/// <summary>Wraps a multi-write operation in a single database transaction, so a failure partway through leaves no partial effect (e.g. EditMeterReading's value update + audit-correction write).</summary>
public interface IUnitOfWork
{
    Task<TResult> ExecuteInTransactionAsync<TResult>(Func<CancellationToken, Task<TResult>> operation, CancellationToken cancellationToken);
}
