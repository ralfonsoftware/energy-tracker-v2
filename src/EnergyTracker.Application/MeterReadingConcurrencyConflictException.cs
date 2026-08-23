namespace EnergyTracker.Application;

/// <summary>Thrown when a Meter Reading edit loses an AD-4 concurrency race.</summary>
public class MeterReadingConcurrencyConflictException(Guid readingId)
    : Exception($"MeterReading '{readingId}' was updated by someone else. Refresh and try again.")
{
    public Guid ReadingId { get; } = readingId;
}
