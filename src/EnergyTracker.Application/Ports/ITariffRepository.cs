using EnergyTracker.Domain;

namespace EnergyTracker.Application.Ports;

public interface ITariffRepository
{
    Task<Tariff> AddAsync(Tariff tariff, CancellationToken cancellationToken);

    Task<Tariff?> FindByIdAsync(Guid tariffId, CancellationToken cancellationToken);

    // Backs the reject-duplicate-ContractStartDate guard (Create and Edit) — pass
    // excludingTariffId when checking from an edit so the entry being edited doesn't collide with
    // itself.
    Task<bool> ExistsWithContractStartDateAsync(
        Guid householdId, DateTimeOffset contractStartDate, Guid? excludingTariffId, CancellationToken cancellationToken);

    // One page of a Household's Tariff entries, most-recent-first (ContractStartDate descending,
    // then CreatedAtUtc descending, then Id descending as the final deterministic tiebreak).
    Task<(IReadOnlyList<Tariff> Items, int TotalCount)> GetHistoryForHouseholdAsync(
        Guid householdId, int page, int pageSize, CancellationToken cancellationToken);

    // Optimistic-concurrency-guarded field edit (AD-4). Only the non-null parameters are applied —
    // EditTariff computes the diff and passes null for every field that didn't change, so a
    // single-field correction never touches the other four columns. Throws
    // TariffConcurrencyConflictException on a Version mismatch — mirrors
    // MeterReadingRepository.UpdateKwhValueAsync's exact mechanics.
    Task<Tariff> UpdateAsync(
        Guid tariffId,
        decimal? monthlyBaseFee,
        decimal? pricePerKwh,
        string? currency,
        DateTimeOffset? contractStartDate,
        int? contractPeriodMonths,
        int expectedVersion,
        CancellationToken cancellationToken);
}
