using EnergyTracker.Domain;
using EnergyTracker.Domain.Calculations;
using Shouldly;

namespace EnergyTracker.Application.Tests;

public class SmartPlugGapDetectorTests
{
    private static readonly Guid HouseholdId = Guid.NewGuid();
    private static readonly Guid SmartPlugImportId = Guid.NewGuid();
    private static readonly Guid PowerPointId = Guid.NewGuid();
    private static readonly DateTimeOffset NowUtc = DateTimeOffset.UtcNow;

    private static SmartPlugReading Reading(DateOnly date, decimal kwh) => new()
    {
        Id = Guid.NewGuid(),
        HouseholdId = HouseholdId,
        SmartPlugImportId = SmartPlugImportId,
        PowerPointId = PowerPointId,
        RoomName = "Kitchen",
        PowerPointName = "Fridge",
        DeviceName = "Fridge",
        IntervalStart = new DateTimeOffset(date.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero),
        IntervalEnd = new DateTimeOffset(date.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero),
        KwhValue = kwh,
    };

    private static IReadOnlyList<SmartPlugImportGap> Detect(
        IReadOnlyList<SmartPlugReading> importReadings, IReadOnlyList<SmartPlugReading>? priorReadings = null)
    {
        var prior = priorReadings ?? [];
        // Mirrors what SmartPlugImportRepository.FindFirstReadingDateByPowerPointAsync would
        // return in production — the earliest date across all of this Power Point's readings
        // (prior + this import's own), or null if there's no history anywhere at all.
        var allDates = prior.Concat(importReadings).Select(r => DateOnly.FromDateTime(r.IntervalStart.DateTime)).ToList();
        DateOnly? firstEverReadingDate = allDates.Count > 0 ? allDates.Min() : null;
        return SmartPlugGapDetector.DetectGaps(HouseholdId, SmartPlugImportId, PowerPointId, importReadings, prior, firstEverReadingDate, NowUtc);
    }

    [Fact]
    public void No_gap_when_every_date_in_the_range_has_a_reading()
    {
        var start = new DateOnly(2026, 8, 1);
        var readings = Enumerable.Range(0, 5).Select(i => Reading(start.AddDays(i), 1m)).ToList();

        var gaps = Detect(readings);

        gaps.ShouldBeEmpty();
    }

    [Fact]
    public void A_0_kWh_reading_is_a_valid_data_point_never_a_gap()
    {
        var start = new DateOnly(2026, 8, 1);
        var readings = new List<SmartPlugReading>
        {
            Reading(start, 1m),
            Reading(start.AddDays(1), 0m),
            Reading(start.AddDays(2), 1m),
        };

        var gaps = Detect(readings);

        gaps.ShouldBeEmpty();
    }

    [Fact]
    public void Multiple_contiguous_missing_dates_collapse_into_one_gap_row()
    {
        // 8 days of real history precede the gap so the "full preceding week" elapsed-time
        // requirement is satisfied — isolates the collapsing behavior from the Missing/Estimated
        // distinction covered by the other tests.
        var start = new DateOnly(2026, 8, 1);
        var readings = Enumerable.Range(0, 8).Select(i => Reading(start.AddDays(i), 4m)).ToList();
        // Aug 9-11 missing, Aug 12 has data (closes the gap).
        readings.Add(Reading(start.AddDays(11), 4m));

        var gaps = Detect(readings);

        gaps.Count.ShouldBe(1);
        gaps[0].StartDate.ShouldBe(start.AddDays(8));
        gaps[0].EndDate.ShouldBe(start.AddDays(10));
    }

    [Fact]
    public void A_mid_range_gap_with_a_full_preceding_week_of_history_is_Estimated_with_the_capped_average()
    {
        var start = new DateOnly(2026, 8, 8);
        // 7 preceding days (Aug 1-7) each have data, averaging (2+3+4+2+3+4+2)/7 = 20/7.
        var precedingKwh = new decimal[] { 2m, 3m, 4m, 2m, 3m, 4m, 2m };
        var readings = new List<SmartPlugReading>();
        for (var i = 0; i < 7; i++)
        {
            readings.Add(Reading(start.AddDays(-7 + i), precedingKwh[i]));
        }

        // Aug 8-9 missing, Aug 10 has data (closes the gap).
        readings.Add(Reading(start.AddDays(2), 5m));

        var gaps = Detect(readings);

        gaps.Count.ShouldBe(1);
        gaps[0].Treatment.ShouldBe(SmartPlugImportGapTreatment.Estimated);
        gaps[0].StartDate.ShouldBe(start);
        gaps[0].EndDate.ShouldBe(start.AddDays(1));
        var expectedDailyAverage = precedingKwh.Average();
        gaps[0].EstimatedTotalKwh!.Value.ShouldBe(expectedDailyAverage * 2, 0.0000001m);
    }

    [Fact]
    public void A_gap_with_fewer_than_a_full_preceding_week_of_history_anywhere_is_Missing()
    {
        // Only 1 day of history exists anywhere (this Power Point's very first-ever reading) —
        // not a genuine preceding week, even though that single day technically "has data".
        var start = new DateOnly(2026, 8, 1);
        var readings = new List<SmartPlugReading> { Reading(start, 3m), Reading(start.AddDays(3), 3m) };

        var gaps = Detect(readings);

        gaps.Count.ShouldBe(1);
        gaps[0].Treatment.ShouldBe(SmartPlugImportGapTreatment.Missing);
        gaps[0].EstimatedTotalKwh.ShouldBeNull();
    }

    [Fact]
    public void A_single_import_can_produce_multiple_disjoint_gaps_in_chronological_order()
    {
        var start = new DateOnly(2026, 8, 1);
        // 10 dense preceding days so both gaps below clear the "full preceding week" gate.
        var readings = Enumerable.Range(0, 10).Select(i => Reading(start.AddDays(i), 4m)).ToList();
        // Aug 11-12 missing, Aug 13 has data, Aug 14-15 missing, Aug 16 has data (closes range).
        readings.Add(Reading(start.AddDays(12), 4m));
        readings.Add(Reading(start.AddDays(15), 4m));

        var gaps = Detect(readings);

        gaps.Count.ShouldBe(2);
        gaps[0].StartDate.ShouldBe(start.AddDays(10));
        gaps[0].EndDate.ShouldBe(start.AddDays(11));
        gaps[0].Treatment.ShouldBe(SmartPlugImportGapTreatment.Estimated);
        gaps[1].StartDate.ShouldBe(start.AddDays(13));
        gaps[1].EndDate.ShouldBe(start.AddDays(14));
        gaps[1].Treatment.ShouldBe(SmartPlugImportGapTreatment.Estimated);
    }

    [Fact]
    public void A_gap_at_the_files_own_start_edge_is_filled_using_prior_persisted_readings_for_the_same_Power_Point()
    {
        // This import's own file has only 1 real day (rangeStart) before its own internal gap —
        // not enough on its own — but 6 days of prior persisted history for this same Power Point
        // (from an earlier import) complete a genuine 7-day preceding week (AC #5's sub-case (b)).
        var rangeStart = new DateOnly(2026, 8, 8);
        var priorReadings = Enumerable.Range(1, 6).Select(i => Reading(rangeStart.AddDays(-i), 4m)).ToList();
        var importReadings = new List<SmartPlugReading>
        {
            Reading(rangeStart, 4m),
            // Aug 9-10 missing, Aug 11 has data (closes the gap).
            Reading(rangeStart.AddDays(3), 5m),
        };

        var gaps = Detect(importReadings, priorReadings);

        gaps.Count.ShouldBe(1);
        gaps[0].Treatment.ShouldBe(SmartPlugImportGapTreatment.Estimated);
        gaps[0].EstimatedTotalKwh!.Value.ShouldBe(4m * 2, 0.0000001m);
    }

    [Fact]
    public void A_gap_at_a_households_first_ever_import_with_no_prior_Power_Point_history_is_Missing()
    {
        var rangeStart = new DateOnly(2026, 8, 1);
        var importReadings = new List<SmartPlugReading>
        {
            Reading(rangeStart, 5m),
            // Aug 2-3 missing, Aug 4 has data.
            Reading(rangeStart.AddDays(3), 5m),
        };

        var gaps = Detect(importReadings, priorReadings: []);

        gaps.Count.ShouldBe(1);
        gaps[0].Treatment.ShouldBe(SmartPlugImportGapTreatment.Missing);
        gaps[0].EstimatedTotalKwh.ShouldBeNull();
    }
}
