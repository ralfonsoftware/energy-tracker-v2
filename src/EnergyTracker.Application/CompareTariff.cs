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
    decimal CandidateAnnualCostBonusIncluded,
    decimal BonusIncludedAnnualSavings,
    bool IsBonusIncludedWorthSwitching,
    bool IsBonusNormalizedWorthSwitching,
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

        // Naive, undecayed comparison (Story 5.3, FR-13) — deliberately does NOT call
        // BonusDecayNormalizer. The whole point of the two-way signal is that this figure and the
        // bonus-normalized one above use two genuinely different formulas: this one applies the full
        // switching bonus once, in year one, exactly as a household would naively read the offer.
        var candidateAnnualCostBonusIncluded = candidateAnnualCostNoBonus - candidateSwitchingBonus;
        var bonusIncludedAnnualSavings = currentAnnualCost - candidateAnnualCostBonusIncluded;

        // FR-13 tie-break: a breakeven (== 0, not just < 0) resolves to "not worth it" — ties favor
        // staying put. Strictly-greater-than, applied identically to both rows (see Dev Notes on why
        // this isn't scoped to the normalized row only, despite the epic AC's literal wording).
        // Rounded to 2 decimals (matching the frontend's own moneyFormat display precision, review
        // round) before the comparison — otherwise a sub-cent positive savings (e.g. €0.003) would
        // read "Worth switching" next to a displayed "0.00", visually indistinguishable from a tie.
        var isBonusIncludedWorthSwitching = decimal.Round(bonusIncludedAnnualSavings, 2, MidpointRounding.AwayFromZero) > 0m;
        var isBonusNormalizedWorthSwitching = decimal.Round(bonusNormalizedAnnualSavings, 2, MidpointRounding.AwayFromZero) > 0m;

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
            CandidateAnnualCostBonusIncluded: candidateAnnualCostBonusIncluded,
            BonusIncludedAnnualSavings: bonusIncludedAnnualSavings,
            IsBonusIncludedWorthSwitching: isBonusIncludedWorthSwitching,
            IsBonusNormalizedWorthSwitching: isBonusNormalizedWorthSwitching,
            IsLowConfidence: statusResult.IsLowConfidence);
    }
}
