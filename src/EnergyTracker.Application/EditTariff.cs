using System.Globalization;
using EnergyTracker.Application.Ports;
using EnergyTracker.Domain;

namespace EnergyTracker.Application;

/// <summary>Edits an existing Tariff entry for the caller's own Household, gating a locked (already-started) entry's price fields behind an explicit server-side override and recording one audit-trail correction note per changed field rather than a silent overwrite (AC #3, #6, #7).</summary>
public class EditTariff(
    ITariffRepository tariffRepository,
    IAuditCorrectionRecorder auditCorrectionRecorder,
    IUnitOfWork unitOfWork)
{
    public async Task<Tariff> ExecuteAsync(
        Guid householdId,
        Guid tariffId,
        decimal? monthlyBaseFee,
        decimal? pricePerKwh,
        string? currency,
        DateTimeOffset? contractStartDate,
        int? contractPeriodMonths,
        int expectedVersion,
        bool overrideConfirmed,
        CancellationToken cancellationToken)
    {
        if (monthlyBaseFee.HasValue)
        {
            TariffValidation.ValidateMonthlyBaseFee(monthlyBaseFee.Value);
        }

        if (pricePerKwh.HasValue)
        {
            TariffValidation.ValidatePricePerKwh(pricePerKwh.Value);
        }

        if (currency is not null)
        {
            TariffValidation.ValidateCurrency(currency);
        }

        if (contractPeriodMonths.HasValue)
        {
            TariffValidation.ValidateContractPeriodMonths(contractPeriodMonths.Value);
        }

        // AD-3's query filter already scopes this to the caller's Household transparently — no
        // manual HouseholdId check, that would be exactly the per-handler filtering AD-3 exists
        // to prevent.
        var tariff = await tariffRepository.FindByIdAsync(tariffId, cancellationToken);
        if (tariff is null)
        {
            throw new TariffNotFoundException(tariffId);
        }

        // Only fields whose submitted value actually differs from the current one count as a
        // real change — a resubmission of an unchanged field is not a correction (mirrors
        // EditMeterReading's no-op-skip discipline, applied per field here instead of per edit).
        var changedMonthlyBaseFee = monthlyBaseFee.HasValue && monthlyBaseFee.Value != tariff.MonthlyBaseFee;
        var changedPricePerKwh = pricePerKwh.HasValue && pricePerKwh.Value != tariff.PricePerKwh;
        var changedCurrency = currency is not null && currency != tariff.Currency;
        var changedContractStartDate = contractStartDate.HasValue && contractStartDate.Value != tariff.ContractStartDate;
        var changedContractPeriodMonths = contractPeriodMonths.HasValue && contractPeriodMonths.Value != tariff.ContractPeriodMonths;

        var hasAnyChange = changedMonthlyBaseFee || changedPricePerKwh || changedCurrency || changedContractStartDate || changedContractPeriodMonths;

        // A no-op save isn't a correction — skip the write entirely rather than bumping Version
        // for nothing, which would otherwise hand out a spurious 409 to anyone else holding the
        // pre-edit Version for this entry.
        if (!hasAnyChange)
        {
            return tariff;
        }

        // Captured BEFORE the repository call below, not read off `tariff` afterward — EF's
        // identity map returns this SAME tracked instance from ITariffRepository.UpdateAsync's own
        // query (same DbContext, same PK), so `tariff`'s properties would otherwise already reflect
        // the NEW values by the time the audit correction is recorded, silently logging "old ==
        // new" (found via a real Api.Tests round-trip, not a mocked-repository test — the
        // NSubstitute-backed EditTariffTests never touches a real, identity-mapped DbContext).
        var oldMonthlyBaseFee = tariff.MonthlyBaseFee;
        var oldPricePerKwh = tariff.PricePerKwh;
        var oldCurrency = tariff.Currency;
        var oldContractStartDate = tariff.ContractStartDate;
        var oldContractPeriodMonths = tariff.ContractPeriodMonths;

        // AC #3: once a Tariff entry's contract has started, its price fields (base fee, price/kWh)
        // are locked behind an explicit override step — enforced server-side, not left to the
        // frontend's own confirmation UI. Currency/dates/period aren't price fields and are never
        // gated by this check.
        var isLocked = tariff.ContractStartDate <= DateTimeOffset.UtcNow;
        if (isLocked && !overrideConfirmed && (changedMonthlyBaseFee || changedPricePerKwh))
        {
            throw new TariffValidationException(
                "This Tariff entry's contract has already started — editing its price fields requires an explicit override confirmation.");
        }

        var updatedTariff = await unitOfWork.ExecuteInTransactionAsync(async ct =>
        {
            var updated = await tariffRepository.UpdateAsync(
                tariffId,
                changedMonthlyBaseFee ? monthlyBaseFee : null,
                changedPricePerKwh ? pricePerKwh : null,
                changedCurrency ? currency : null,
                changedContractStartDate ? contractStartDate : null,
                changedContractPeriodMonths ? contractPeriodMonths : null,
                expectedVersion,
                ct);

            if (changedMonthlyBaseFee)
            {
                await auditCorrectionRecorder.RecordAsync(
                    householdId, "Tariff", tariffId, "MonthlyBaseFee",
                    oldMonthlyBaseFee.ToString(CultureInfo.InvariantCulture),
                    monthlyBaseFee!.Value.ToString(CultureInfo.InvariantCulture), ct);
            }

            if (changedPricePerKwh)
            {
                await auditCorrectionRecorder.RecordAsync(
                    householdId, "Tariff", tariffId, "PricePerKwh",
                    oldPricePerKwh.ToString(CultureInfo.InvariantCulture),
                    pricePerKwh!.Value.ToString(CultureInfo.InvariantCulture), ct);
            }

            if (changedCurrency)
            {
                await auditCorrectionRecorder.RecordAsync(
                    householdId, "Tariff", tariffId, "Currency", oldCurrency, currency!, ct);
            }

            if (changedContractStartDate)
            {
                await auditCorrectionRecorder.RecordAsync(
                    householdId, "Tariff", tariffId, "ContractStartDate",
                    oldContractStartDate.ToString("O", CultureInfo.InvariantCulture),
                    contractStartDate!.Value.ToString("O", CultureInfo.InvariantCulture), ct);
            }

            if (changedContractPeriodMonths)
            {
                await auditCorrectionRecorder.RecordAsync(
                    householdId, "Tariff", tariffId, "ContractPeriodMonths",
                    oldContractPeriodMonths.ToString(CultureInfo.InvariantCulture),
                    contractPeriodMonths!.Value.ToString(CultureInfo.InvariantCulture), ct);
            }

            return updated;
        }, cancellationToken);

        return updatedTariff;
    }
}
