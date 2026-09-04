namespace BulkWriteThroughputSpike;

// Mirrors SmartPlugImport just enough for a real FK relationship to exist (AC #2) — not a
// full reproduction of the production entity's fields.
public class SpikeSmartPlugImport
{
    public Guid Id { get; set; }
    public Guid HouseholdId { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
}

// Mirrors SmartPlugReading's real column shape (SmartPlugReadingConfiguration.cs) — same types/
// widths for the columns that determine bulk-copy row byte-width and the two AD-23 match-key
// indexes. RoomName/PowerPointName/DeviceName are fixed-length here (see DataGenerator) even
// though production declares them nvarchar(max)/text (unbounded) — production has no declared
// max width to mirror literally, so this spike instead fixes each to a representative real-world
// length (documented in README.md) so every synthetic row pays a consistent, realistic byte cost.
public class SpikeSmartPlugReading
{
    public Guid Id { get; set; }
    public Guid HouseholdId { get; set; }
    public Guid? PowerPointId { get; set; }
    public DateTimeOffset IntervalStart { get; set; }
    public DateTimeOffset IntervalEnd { get; set; }
    public decimal KwhValue { get; set; }
    public required string RoomName { get; set; }
    public required string PowerPointName { get; set; }
    public required string DeviceName { get; set; }
    public Guid? SmartPlugImportId { get; set; }
}
