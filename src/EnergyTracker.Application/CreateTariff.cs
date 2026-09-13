using EnergyTracker.Application.Ports;
using EnergyTracker.Domain;

namespace EnergyTracker.Application;

/// <summary>Creates a new Tariff entry for the caller's own Household, appending to history rather than replacing any prior entry (AC #1, #2, #4, #5).</summary>
public class CreateTariff(ITariffRepository repository)
{
    public async Task<Tariff> ExecuteAsync(
        Guid householdId,
        decimal monthlyBaseFee,
        decimal pricePerKwh,
        string currency,
        DateTimeOffset contractStartDate,
        int contractPeriodMonths,
        CancellationToken cancellationToken)
    {
        TariffValidation.ValidateMonthlyBaseFee(monthlyBaseFee);
        TariffValidation.ValidatePricePerKwh(pricePerKwh);
        TariffValidation.ValidateCurrency(currency);
        TariffValidation.ValidateContractStartDate(contractStartDate);
        TariffValidation.ValidateContractPeriodMonths(contractPeriodMonths);

        // Two entries sharing a ContractStartDate would make "current"/history ordering depend on
        // creation order alone — reject outright rather than accept an ambiguous history.
        if (await repository.ExistsWithContractStartDateAsync(householdId, contractStartDate, excludingTariffId: null, cancellationToken))
        {
            throw new TariffValidationException(
                $"A Tariff entry already exists with ContractStartDate '{contractStartDate:O}' for this Household.");
        }

        // A pure append — never mutates or closes out a prior Tariff entry (AC #2). "Current
        // Tariff" and "effective-until" are computed at read time (GetTariffHistory), not stored.
        var tariff = new Tariff
        {
            Id = Guid.NewGuid(),
            HouseholdId = householdId,
            MonthlyBaseFee = monthlyBaseFee,
            PricePerKwh = pricePerKwh,
            Currency = currency,
            ContractStartDate = contractStartDate,
            ContractPeriodMonths = contractPeriodMonths,
            CreatedAtUtc = DateTimeOffset.UtcNow,
        };

        return await repository.AddAsync(tariff, cancellationToken);
    }
}
