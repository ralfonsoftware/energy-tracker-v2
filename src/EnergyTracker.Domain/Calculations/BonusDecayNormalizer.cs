namespace EnergyTracker.Domain.Calculations;

// AD-5: the ONE shared Bonus-Decay Normalization module, called by both Pattern Detective (this
// story, zero bonus terms) and Story 5.2's Tariff Savings Radar (real switching-bonus terms,
// later epic) — neither feature may reimplement or locally adjust this formula.
//
// A pure function of (rate, bonus terms, elapsed time), per AD-5's exact wording. `annualRateKwh`
// is an annualized figure (e.g. Household.YearlyBaselineKwh); `bonusTermsKwh` is a one-off amount
// assumed front-loaded at elapsed = 0 and linearly decayed to zero over the same one-year window
// it's normalized against, so it never distorts a partial-period comparison. This story always
// calls it with bonusTermsKwh = 0, which makes the decay term a no-op and the whole function
// degenerate to a straight day-count proration of the annual rate — Story 5.2 is the first real
// caller of the bonus-decay behavior itself.
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
