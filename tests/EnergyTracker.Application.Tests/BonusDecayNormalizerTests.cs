using EnergyTracker.Domain.Calculations;
using Shouldly;

namespace EnergyTracker.Application.Tests;

public class BonusDecayNormalizerTests
{
    [Fact]
    public void Half_a_year_elapsed_with_no_bonus_terms_prorates_the_annual_rate_by_half()
    {
        var result = BonusDecayNormalizer.NormalizeToDate(3650m, 0m, TimeSpan.FromDays(182.5));

        result.ShouldBe(1825m);
    }

    [Fact]
    public void Zero_elapsed_time_with_no_bonus_terms_normalizes_to_zero()
    {
        var result = BonusDecayNormalizer.NormalizeToDate(3650m, 0m, TimeSpan.Zero);

        result.ShouldBe(0m);
    }

    [Fact]
    public void A_bonus_term_decays_linearly_to_zero_over_the_one_year_window()
    {
        var atStart = BonusDecayNormalizer.NormalizeToDate(0m, 365m, TimeSpan.Zero);
        var atHalfway = BonusDecayNormalizer.NormalizeToDate(0m, 365m, TimeSpan.FromDays(182.5));
        var atYearEnd = BonusDecayNormalizer.NormalizeToDate(0m, 365m, TimeSpan.FromDays(365));

        atStart.ShouldBe(365m);
        atHalfway.ShouldBe(182.5m);
        atYearEnd.ShouldBe(0m);
    }

    [Fact]
    public void Negative_elapsed_time_throws()
    {
        Should.Throw<ArgumentOutOfRangeException>(() =>
            BonusDecayNormalizer.NormalizeToDate(1000m, 0m, TimeSpan.FromDays(-1)));
    }
}
