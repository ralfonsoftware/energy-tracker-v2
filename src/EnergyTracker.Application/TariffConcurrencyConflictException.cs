namespace EnergyTracker.Application;

/// <summary>Thrown when a Tariff edit loses an AD-4 concurrency race.</summary>
public class TariffConcurrencyConflictException(Guid tariffId)
    : Exception($"Tariff '{tariffId}' was updated by someone else. Refresh and try again.")
{
    public Guid TariffId { get; } = tariffId;
}
