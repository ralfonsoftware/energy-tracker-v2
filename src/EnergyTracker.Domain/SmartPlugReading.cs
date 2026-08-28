namespace EnergyTracker.Domain;

// AD-10: snapshots Room/Power Point/Device identity BY VALUE at write time — a later retag must
// never rewrite this reading's historical attribution via a live FK join. HouseholdId/
// SmartPlugImportId/PowerPointId/RoomName/PowerPointName are mutable-via-init-only-elsewhere
// (not `init`) because ISmartPlugParser.Parse produces these readings before the Household,
// SmartPlugImport, or a Power Point match are known — ProcessSmartPlugImport fills them in
// once parsing and matching are done, same "mutable via X only" discipline as PowerPoint.RoomId.
public class SmartPlugReading
{
    public required Guid Id { get; init; }

    public required Guid HouseholdId { get; set; }

    // Nullable so the sweep (Story 3.6/AD-6 extension) can delete a swept-away SmartPlugImport
    // row while this reading survives (SetNull FK) — AD-20's "detach, never delete" rule for
    // Smart Plug data.
    public Guid? SmartPlugImportId { get; set; }

    public Guid? PowerPointId { get; set; }

    public required string RoomName { get; set; }

    public required string PowerPointName { get; set; }

    // The device tag as parsed from the file — distinct from Device.Name (this story only
    // matches at Power Point granularity, per Task 3; no Device entity is resolved).
    public required string DeviceName { get; init; }

    public required DateTimeOffset IntervalStart { get; init; }

    public required DateTimeOffset IntervalEnd { get; init; }

    public required decimal KwhValue { get; init; }
}
