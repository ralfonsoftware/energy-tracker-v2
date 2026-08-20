namespace EnergyTracker.Domain.Calculations;

// Pure, static — same shape as PatternDetectiveCalculator/BonusDecayNormalizer. Must never
// import/reference MeterReading, Status, StatusSnapshot, or anything under Application/Api — a
// one-way dependency (Smart Plug data flows toward Status via the sharpening signal, Task 4, never
// the reverse); lives entirely outside the AD-14 guard test's file list.
//
// AD-9: operates purely on SmartPlugReading.IntervalStart's local-time date (already normalized by
// each vendor's ISmartPlugParser adapter — Eve Home ~10-minute point samples, IntervalStart ==
// IntervalEnd; Meross one full-day interval per row, both ends the same calendar day). No
// vendor-specific logic belongs here.
public static class SmartPlugGapDetector
{
    // Public so callers (CompleteSmartPlugImportProcessing) can bound their own prior-readings
    // query to this same window instead of loading a Power Point's full reading history.
    public const int TrailingAverageWindowDays = 7;

    // Confirmed with Ralf during dev-story activation: the "no preceding history" check (AC #6) is
    // scoped per Power Point, not per household — `priorReadings` already carries only this Power
    // Point's history across earlier imports (caller's responsibility, see
    // CompleteSmartPlugImportProcessing), so a Household's 2nd/3rd/... Power Point can each be
    // "first-ever" independently.
    //
    // Design note (not in the story's literal text, worked out during implementation — a detected
    // gap's immediately-preceding calendar day always has a reading by construction, since that's
    // exactly what makes it the gap's boundary; "does at least one of the 7 preceding days have
    // data" would therefore always be true and Missing would be unreachable). The rule actually
    // applied: Estimated requires a genuine full preceding week to have elapsed since this Power
    // Point's earliest-ever known reading (prior import history, or — if none — this file's own
    // first date); fewer than 7 calendar days of real history behind the gap is "no preceding week
    // to average" (AC #6), regardless of how sparse or dense that short stretch is. Once a full
    // week has elapsed, the fill amount itself still averages only whichever of the literal 7
    // trailing days actually have data (AC #5's "capped at the preceding week's average").
    public static IReadOnlyList<SmartPlugImportGap> DetectGaps(
        Guid householdId,
        Guid smartPlugImportId,
        Guid powerPointId,
        IReadOnlyList<SmartPlugReading> importReadings,
        IReadOnlyList<SmartPlugReading> priorReadings,
        DateOnly? firstEverReadingDate,
        DateTimeOffset nowUtc)
    {
        if (importReadings.Count == 0)
        {
            return [];
        }

        // Presence-only set for defining the gap range itself (AC #4: a 0 kWh reading is a valid
        // data point, never a gap — checked by row presence, never by summing to zero).
        var importDatesWithData = importReadings.Select(DateOf).ToHashSet();

        // Merged daily totals (import + prior, same Power Point) used only for the trailing-average
        // fill computation below — irrelevant to whether a date counts as a gap. `priorReadings` is
        // expected to already be bounded to the trailing window a caller can actually need (see
        // CompleteSmartPlugImportProcessing) — the true "has any history ever existed" question is
        // answered by `firstEverReadingDate` below, not by scanning `priorReadings` itself.
        var dailyTotals = new Dictionary<DateOnly, decimal>();
        foreach (var reading in priorReadings.Concat(importReadings))
        {
            var date = DateOf(reading);
            dailyTotals[date] = dailyTotals.GetValueOrDefault(date) + reading.KwhValue;
        }

        var rangeStart = importDatesWithData.Min();
        var rangeEnd = importDatesWithData.Max();

        // `firstEverReadingDate` is the Power Point's true earliest-ever reading date across ALL of
        // its history (a cheap indexed MIN lookup at the caller, not derived from `priorReadings`,
        // which may be windowed) — `null` only for a genuinely first-ever import with no prior
        // reading anywhere yet, in which case this import's own first date is the earliest.
        var firstEverDate = firstEverReadingDate ?? rangeStart;
        if (firstEverDate > rangeStart)
        {
            firstEverDate = rangeStart;
        }

        var gaps = new List<SmartPlugImportGap>();
        DateOnly? contiguousGapStart = null;

        for (var date = rangeStart; date <= rangeEnd; date = date.AddDays(1))
        {
            var isGapDate = !importDatesWithData.Contains(date);
            if (isGapDate && contiguousGapStart is null)
            {
                contiguousGapStart = date;
            }
            else if (!isGapDate && contiguousGapStart is { } gapStart)
            {
                gaps.Add(BuildGap(gapStart, date.AddDays(-1)));
                contiguousGapStart = null;
            }
        }

        // No trailing-gap-close branch after the loop: `rangeEnd` is defined above as the max date
        // *with* data, so the loop's final iteration (date == rangeEnd) is always a non-gap date —
        // any open gap is always already closed by the `else if` branch inside the loop.

        return gaps;

        SmartPlugImportGap BuildGap(DateOnly gapStart, DateOnly gapEnd)
        {
            var gapDayCount = gapEnd.DayNumber - gapStart.DayNumber + 1;
            var elapsedDaysSinceFirstEverReading = gapStart.DayNumber - firstEverDate.DayNumber;

            if (elapsedDaysSinceFirstEverReading < TrailingAverageWindowDays)
            {
                return new SmartPlugImportGap
                {
                    Id = Guid.NewGuid(),
                    HouseholdId = householdId,
                    SmartPlugImportId = smartPlugImportId,
                    PowerPointId = powerPointId,
                    StartDate = gapStart,
                    EndDate = gapEnd,
                    Treatment = SmartPlugImportGapTreatment.Missing,
                    EstimatedTotalKwh = null,
                    CreatedAtUtc = nowUtc,
                };
            }

            var precedingDaysWithData = new List<decimal>();
            for (var day = gapStart.AddDays(-TrailingAverageWindowDays); day < gapStart; day = day.AddDays(1))
            {
                if (dailyTotals.TryGetValue(day, out var total))
                {
                    precedingDaysWithData.Add(total);
                }
            }

            var cappedDailyAverage = precedingDaysWithData.Average();
            return new SmartPlugImportGap
            {
                Id = Guid.NewGuid(),
                HouseholdId = householdId,
                SmartPlugImportId = smartPlugImportId,
                PowerPointId = powerPointId,
                StartDate = gapStart,
                EndDate = gapEnd,
                Treatment = SmartPlugImportGapTreatment.Estimated,
                EstimatedTotalKwh = cappedDailyAverage * gapDayCount,
                CreatedAtUtc = nowUtc,
            };
        }
    }

    private static DateOnly DateOf(SmartPlugReading reading) => DateOnly.FromDateTime(reading.IntervalStart.DateTime);
}
