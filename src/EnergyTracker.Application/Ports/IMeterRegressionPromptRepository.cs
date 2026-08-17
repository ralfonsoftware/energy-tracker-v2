using EnergyTracker.Domain;

namespace EnergyTracker.Application.Ports;

public interface IMeterRegressionPromptRepository
{
    Task<MeterRegressionPrompt> AddAsync(MeterRegressionPrompt prompt, CancellationToken cancellationToken);

    // "Open" is computed, not stored: the unresolved prompt with the earliest MeterReading.ReadingTimestamp
    // for the Household's Main Meter (AD-12 — at most one open prompt, ordered by reading timestamp).
    Task<MeterRegressionPrompt?> GetOpenForHouseholdAsync(Guid householdId, CancellationToken cancellationToken);

    Task<MeterRegressionPrompt?> FindByIdAsync(Guid householdId, Guid promptId, CancellationToken cancellationToken);

    // All resolved (Classification/DigitCapacityKwh/ResolvedAtUtc set) prompts for one Main
    // Meter — Story 2.4's pace walk needs every past correction, not just the currently open one.
    Task<IReadOnlyList<MeterRegressionPrompt>> GetResolvedForMainMeterAsync(Guid mainMeterId, CancellationToken cancellationToken);

    // Persists a prompt whose Classification/DigitCapacityKwh/ResolvedAtUtc the caller has already set,
    // via a conditional UPDATE ... WHERE Id = @id AND ResolvedAtUtc IS NULL. Returns false (without writing)
    // if the prompt was resolved by a concurrent request in between the caller's read and this write —
    // the caller must not assume its in-memory prompt instance was actually persisted just because no
    // exception was thrown.
    Task<bool> ResolveAsync(MeterRegressionPrompt prompt, CancellationToken cancellationToken);

    Task<decimal?> GetMainMeterDigitCapacityAsync(Guid mainMeterId, CancellationToken cancellationToken);

    // Conditional UPDATE ... WHERE DigitCapacityKwh IS NULL — never overwrites an already-confirmed
    // capacity from a later resolution.
    Task SetMainMeterDigitCapacityIfUnsetAsync(Guid mainMeterId, decimal digitCapacityKwh, CancellationToken cancellationToken);
}
