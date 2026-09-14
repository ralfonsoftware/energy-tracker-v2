namespace EnergyTracker.Domain.Calculations;

// AD-5: the ONE shared Bonus-Decay Normalization module, called by both Pattern Detective
// (GetCurrentStatus.cs, zero bonus terms — Household.YearlyBaselineKwh normalized to the pace
// window) and Tariff Savings Radar's CompareTariff.cs (real switching-bonus terms, money units
// instead of kWh) — neither feature may reimplement or locally adjust this formula.
//
// A pure function of (rate, bonus terms, elapsed time), per AD-5's exact wording — unit-agnostic,
// despite the Kwh-suffixed parameter names (a historical artifact of Pattern Detective being the
// first caller; CompareTariff.cs passes money amounts positionally, not kWh). `bonusTermsKwh` is
// a one-off amount assumed front-loaded at elapsed = 0 and linearly decayed to zero over the same
// one-year window it's normalized against, so it never distorts a partial-period comparison.
// Pattern Detective always calls it with bonusTermsKwh = 0 (the decay term is then a no-op, and
// the whole function degenerates to a straight day-count proration of the annual rate);
// CompareTariff.cs is the first real caller of the bonus-decay behavior itself, called at
// elapsed = 365 days exactly so the bonus term is always fully decayed in an annual projection.
public static class BonusDecayNormalizer
{
    private const decimal DaysPerYear = 365m;

    public static decimal NormalizeToDate(decimal annualRateKwh, decimal bonusTermsKwh, TimeSpan elapsed)
    {
        if (elapsed < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(elapsed), elapsed, "Elapsed time cannot be negative.");
        }

        var elapsedFractionOfYear = (decimal)elapsed.TotalDays / DaysPerYear;
        var decayedBonusKwh = bonusTermsKwh * Math.Max(0m, 1m - elapsedFractionOfYear);

        return annualRateKwh * elapsedFractionOfYear + decayedBonusKwh;
    }
}
