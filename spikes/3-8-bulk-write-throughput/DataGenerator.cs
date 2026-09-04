namespace BulkWriteThroughputSpike;

// Generates the four synthetic sizing tiers from the story's Context section (AC #3). Every batch
// uses a fixed RNG seed so re-running against a second provider (or re-running after a failed
// attempt) reproduces byte-identical data — a fair, comparable throughput measurement across
// Postgres and SQL Server. Nothing here reads sample-data/eve or sample-data/meross; all values
// are purely synthetic, matching only the *statistical shape* (row counts, cadence, value ranges)
// documented in the story.
public static class DataGenerator
{
    // Fixed-width RoomName/PowerPointName/DeviceName columns (see SchemaSql.cs — nchar/char, true
    // fixed-width DB types, not just a declared max on a variable-width type, since production's
    // own SmartPlugReadingConfiguration.cs declares these nvarchar(max)/text with no real max to
    // mirror). Lengths below are a representative real-world upper bound, not derived from a
    // specific measured value — documented assumption, see README.md.
    public const int RoomNameLength = 20;
    public const int PowerPointNameLength = 30;
    public const int DeviceNameLength = 40;

    private static readonly string[] RoomNames =
        ["Living Room", "Kitchen", "Bedroom", "Home Office", "Garage", "Hallway", "Bathroom", "Basement"];

    private static readonly string[] PowerPointSuffixes =
        ["TV Outlet", "Fridge Circuit", "Router Outlet", "Washer Circuit", "Desk Outlet", "Heater Circuit"];

    private static readonly string[] DevicePrefixes =
        ["Eve Energy", "Meross Smart Plug", "Eve Energy Strip", "Meross MSS310"];

    // Eve Home's real ~10-minute sampling cadence (Context section).
    private static readonly TimeSpan SampleInterval = TimeSpan.FromMinutes(10);

    private static SpikeSmartPlugReading MakeReading(
        Random rng,
        Guid householdId,
        Guid? powerPointId,
        DateTimeOffset intervalStart,
        Guid? smartPlugImportId)
    {
        var room = RoomNames[rng.Next(RoomNames.Length)];
        var powerPoint = $"{room} - {PowerPointSuffixes[rng.Next(PowerPointSuffixes.Length)]}";
        var device = $"{DevicePrefixes[rng.Next(DevicePrefixes.Length)]} ({room})";

        // Plausible small-appliance range per 10-minute interval — Eve Home's own real sample
        // (0.00082) sits comfortably inside this; never a constant value across rows (AC #3).
        var kwh = (decimal)(rng.NextDouble() * 0.0095 + 0.00005);

        return new SpikeSmartPlugReading
        {
            Id = Guid.NewGuid(),
            HouseholdId = householdId,
            PowerPointId = powerPointId,
            IntervalStart = intervalStart,
            IntervalEnd = intervalStart + SampleInterval,
            KwhValue = Math.Round(kwh, 6),
            RoomName = room,
            PowerPointName = powerPoint,
            DeviceName = device,
            SmartPlugImportId = smartPlugImportId,
        };
    }

    // ~120,000-row single-device/first-full-import batch (AC #3a). One PowerPointId, ~10-minute
    // cadence walking back from `endExclusive` far enough to hit rowCount rows.
    public static IEnumerable<SpikeSmartPlugReading> GenerateSingleDeviceBatch(
        Guid householdId, Guid powerPointId, Guid? smartPlugImportId,
        DateTimeOffset endExclusive, int rowCount = 120_000, int seed = 380_120_000)
    {
        var rng = new Random(seed);
        var start = endExclusive - SampleInterval * rowCount;
        for (var i = 0; i < rowCount; i++)
        {
            yield return MakeReading(rng, householdId, powerPointId, start + SampleInterval * i, smartPlugImportId);
        }
    }

    // ~470,000-row pre-load baseline (AC #3b). Several distinct PowerPointIds (multi-device
    // household), interval timestamps spread across a multi-year span.
    public static IEnumerable<SpikeSmartPlugReading> GeneratePreloadBatch(
        Guid householdId, IReadOnlyList<Guid> powerPointIds, Guid? smartPlugImportId,
        DateTimeOffset endExclusive, int rowCount = 470_000, int seed = 380_470_000)
    {
        var rng = new Random(seed);
        var rowsPerDevice = rowCount / powerPointIds.Count;
        var remainder = rowCount - rowsPerDevice * powerPointIds.Count;

        for (var d = 0; d < powerPointIds.Count; d++)
        {
            var deviceRowCount = rowsPerDevice + (d == 0 ? remainder : 0);
            var start = endExclusive - SampleInterval * deviceRowCount;
            for (var i = 0; i < deviceRowCount; i++)
            {
                yield return MakeReading(rng, householdId, powerPointIds[d], start + SampleInterval * i, smartPlugImportId);
            }
        }
    }

    // ~500-row typical-incremental-delta batch (AC #3c). `overlapExisting` supplies IntervalStart
    // values already present in the pre-loaded table so a realistic fraction of the delta
    // genuinely collides (mirrors a real incremental re-import), the rest are new timestamps past
    // the existing watermark.
    public static IEnumerable<SpikeSmartPlugReading> GenerateIncrementalDeltaBatch(
        Guid householdId, Guid powerPointId, Guid? smartPlugImportId,
        IReadOnlyList<DateTimeOffset> overlapExisting, DateTimeOffset newRowsStart,
        int rowCount = 500, int seed = 380_000_500)
    {
        var rng = new Random(seed);
        var overlapCount = Math.Min(overlapExisting.Count, rowCount / 2);
        for (var i = 0; i < overlapCount; i++)
        {
            yield return MakeReading(rng, householdId, powerPointId, overlapExisting[i], smartPlugImportId);
        }
        for (var i = 0; i < rowCount - overlapCount; i++)
        {
            yield return MakeReading(rng, householdId, powerPointId, newRowsStart + SampleInterval * i, smartPlugImportId);
        }
    }

    // ~5,000-row PowerPointId IS NULL batch (AC #3d, AwaitingPowerPointMapping scale).
    public static IEnumerable<SpikeSmartPlugReading> GenerateNullPowerPointBatch(
        Guid householdId, Guid? smartPlugImportId,
        DateTimeOffset endExclusive, int rowCount = 5_000, int seed = 380_005_000)
    {
        var rng = new Random(seed);
        var start = endExclusive - SampleInterval * rowCount;
        for (var i = 0; i < rowCount; i++)
        {
            yield return MakeReading(rng, householdId, null, start + SampleInterval * i, smartPlugImportId);
        }
    }
}
