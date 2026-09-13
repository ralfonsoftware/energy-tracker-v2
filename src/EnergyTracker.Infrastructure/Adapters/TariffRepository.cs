using EnergyTracker.Application;
using EnergyTracker.Application.Ports;
using EnergyTracker.Domain;
using Microsoft.EntityFrameworkCore;

namespace EnergyTracker.Infrastructure.Adapters;

public class TariffRepository(EnergyTrackerDbContext dbContext) : ITariffRepository
{
    public async Task<Tariff> AddAsync(Tariff tariff, CancellationToken cancellationToken)
    {
        await dbContext.Tariffs.AddAsync(tariff, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
        return tariff;
    }

    public Task<Tariff?> FindByIdAsync(Guid tariffId, CancellationToken cancellationToken) =>
        dbContext.Tariffs.SingleOrDefaultAsync(t => t.Id == tariffId, cancellationToken);

    public async Task<(IReadOnlyList<Tariff> Items, int TotalCount)> GetHistoryForHouseholdAsync(
        Guid householdId, int page, int pageSize, CancellationToken cancellationToken)
    {
        var query = dbContext.Tariffs.Where(t => t.HouseholdId == householdId);

        var totalCount = await query.CountAsync(cancellationToken);
        var items = await query
            .OrderByDescending(t => t.ContractStartDate)
            .ThenByDescending(t => t.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);

        return (items, totalCount);
    }

    public async Task<Tariff> UpdateAsync(
        Guid tariffId,
        decimal? monthlyBaseFee,
        decimal? pricePerKwh,
        string? currency,
        DateTimeOffset? contractStartDate,
        int? contractPeriodMonths,
        int expectedVersion,
        CancellationToken cancellationToken)
    {
        var tariff = await dbContext.Tariffs.SingleAsync(t => t.Id == tariffId, cancellationToken);

        // Makes EF's SaveChangesAsync compare expectedVersion (the caller's known value) against
        // the DB, not whatever the freshly-loaded entity already has.
        dbContext.Entry(tariff).Property(t => t.Version).OriginalValue = expectedVersion;

        // Only the non-null parameters are applied — EditTariff already computed the diff, so a
        // single-field correction never touches the other four columns.
        if (monthlyBaseFee.HasValue)
        {
            tariff.MonthlyBaseFee = monthlyBaseFee.Value;
        }

        if (pricePerKwh.HasValue)
        {
            tariff.PricePerKwh = pricePerKwh.Value;
        }

        if (currency is not null)
        {
            tariff.Currency = currency;
        }

        if (contractStartDate.HasValue)
        {
            tariff.ContractStartDate = contractStartDate.Value;
        }

        if (contractPeriodMonths.HasValue)
        {
            tariff.ContractPeriodMonths = contractPeriodMonths.Value;
        }

        // AD-4 requires the concurrency token to change on every update — same reasoning as
        // MeterReadingRepository.UpdateKwhValueAsync's reading.Version++.
        tariff.Version++;

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new TariffConcurrencyConflictException(tariffId);
        }

        return tariff;
    }
}
