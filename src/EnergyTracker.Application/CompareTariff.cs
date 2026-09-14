using EnergyTracker.Application.Ports;
using EnergyTracker.Domain.Calculations;

namespace EnergyTracker.Application;

public record TariffComparisonResult(
    decimal CurrentMonthlyBaseFee,
    decimal CurrentPricePerKwh,
    string CurrentCurrency,
    decimal CandidateMonthlyBaseFee,
    decimal CandidatePricePerKwh,
    decimal CandidateSwitchingBonus,
    decimal AnnualPaceKwh,
    decimal CurrentAnnualCost,
    decimal CandidateAnnualCostBonusNormalized,
    decimal BonusNormalizedAnnualSavings,
    bool IsLowConfidence);

/// <summary>Computes a scratch/exploratory candidate Tariff's bonus-decay normalized annual savings against the Household's current Tariff, using actual Pattern Detective pace (FR-11, FR-12, FR-14, AD-5).</summary>
public class CompareTariff(GetCurrentStatus getCurrentStatus, ITariffRepository tariffRepository)
{
    public async Task<TariffComparisonResult?> ExecuteAsync(
        Guid householdId,
        decimal candidateMonthlyBaseFee,
        decimal candidatePricePerKwh,
        decimal candidateSwitchingBonus,
        CancellationToken cancellationToken)
    {
        TariffValidation.ValidateMonthlyBaseFee(candidateMonthlyBaseFee);
        TariffValidation.ValidatePricePerKwh(candidatePricePerKwh);
        TariffValidation.ValidateSwitchingBonus(candidateSwitchingBonus);

        var currentTariff = await tariffRepository.FindCurrentForHouseholdAsync(householdId, cancellationToken);
        if (currentTariff is null)
        {
            return null;
        }

        var statusResult = await getCurrentStatus.ExecuteAsync(householdId, cancellationToken);
        if (statusResult is null)
        {
            return null;
        }

        // New logic (nothing in PatternDetectiveCalculator/GetCurrentStatus does this today —
        // Status only ever compares pace-to-date against baseline-to-date over the *same* window).
        // ElapsedDays is guaranteed > 0 whenever GetCurrentStatus returns non-null.
        var annualPaceKwh = statusResult.PaceToDateKwh * 365m / (decimal)statusResult.ElapsedDays;

        var currentAnnualCost = currentTariff.MonthlyBaseFee * 12 + currentTariff.PricePerKwh * annualPaceKwh;
        var candidateAnnualCostNoBonus = candidateMonthlyBaseFee * 12 + candidatePricePerKwh * annualPaceKwh;

        // AD-5: the single shared Bonus-Decay Normalization module — at elapsed = 365 days the
        // bonus term is fully decayed to 0, which is exactly what an "annual" projection needs.
        // Never simplify this to a direct assignment: a future decay-formula change (AC #5) must
        // still reach this call site automatically.
        var bonusNormalizedCandidateAnnualCost = BonusDecayNormalizer.NormalizeToDate(
            candidateAnnualCostNoBonus, candidateSwitchingBonus, TimeSpan.FromDays(365));

        var bonusNormalizedAnnualSavings = currentAnnualCost - bonusNormalizedCandidateAnnualCost;

        return new TariffComparisonResult(
            CurrentMonthlyBaseFee: currentTariff.MonthlyBaseFee,
            CurrentPricePerKwh: currentTariff.PricePerKwh,
            CurrentCurrency: currentTariff.Currency,
            CandidateMonthlyBaseFee: candidateMonthlyBaseFee,
            CandidatePricePerKwh: candidatePricePerKwh,
            CandidateSwitchingBonus: candidateSwitchingBonus,
            AnnualPaceKwh: annualPaceKwh,
            CurrentAnnualCost: currentAnnualCost,
            CandidateAnnualCostBonusNormalized: bonusNormalizedCandidateAnnualCost,
            BonusNormalizedAnnualSavings: bonusNormalizedAnnualSavings,
            IsLowConfidence: statusResult.IsLowConfidence);
    }
}
