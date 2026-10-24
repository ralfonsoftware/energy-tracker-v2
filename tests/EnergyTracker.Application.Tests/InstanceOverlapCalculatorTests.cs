using EnergyTracker.Domain.Calculations;
using Shouldly;

namespace EnergyTracker.Application.Tests;

// Story 11.13 (AD-24, AC #7): the overlap of two instance heartbeat rows is
// min(last seen) - max(started), never negative. Pure: no clock, no I/O.
public class InstanceOverlapCalculatorTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 24, 12, 0, 0, TimeSpan.Zero);

    private static DateTimeOffset At(int minutes) => T0.AddMinutes(minutes);

    [Fact]
    public void Two_instances_that_never_ran_at_the_same_time_overlap_for_zero()
    {
        // Self ran 0..10; the other started at 20.
        var overlap = InstanceOverlapCalculator.Overlap(At(0), At(10), At(20), At(30));

        overlap.ShouldBe(TimeSpan.Zero);
    }

    [Fact]
    public void A_partial_overlap_is_the_shared_stretch()
    {
        // Self ran 0..20; the other 12..40: shared stretch 12..20.
        var overlap = InstanceOverlapCalculator.Overlap(At(0), At(20), At(12), At(40));

        overlap.ShouldBe(TimeSpan.FromMinutes(8));
    }

    [Fact]
    public void When_the_other_started_earlier_the_overlap_starts_at_self_start()
    {
        // Self ran 10..40; the other 0..25: shared stretch 10..25.
        var overlap = InstanceOverlapCalculator.Overlap(At(10), At(40), At(0), At(25));

        overlap.ShouldBe(TimeSpan.FromMinutes(15));
    }

    [Fact]
    public void When_self_started_earlier_the_overlap_starts_at_the_others_start()
    {
        // Self ran 0..40; the other 10..25: shared stretch 10..25.
        var overlap = InstanceOverlapCalculator.Overlap(At(0), At(40), At(10), At(25));

        overlap.ShouldBe(TimeSpan.FromMinutes(15));
    }

    [Fact]
    public void Equal_start_times_overlap_until_the_earlier_last_seen()
    {
        var overlap = InstanceOverlapCalculator.Overlap(At(5), At(30), At(5), At(18));

        overlap.ShouldBe(TimeSpan.FromMinutes(13));
    }

    [Fact]
    public void A_last_seen_before_its_own_start_is_clock_skew_and_gives_zero_never_negative()
    {
        // Self "last seen" 3 minutes before it "started".
        var overlap = InstanceOverlapCalculator.Overlap(At(10), At(7), At(0), At(30));

        overlap.ShouldBe(TimeSpan.Zero);
    }

    [Fact]
    public void Instances_that_touch_at_one_instant_overlap_for_zero()
    {
        var overlap = InstanceOverlapCalculator.Overlap(At(0), At(10), At(10), At(20));

        overlap.ShouldBe(TimeSpan.Zero);
    }
}
