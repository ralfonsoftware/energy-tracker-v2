using EnergyTracker.Domain;

namespace EnergyTracker.Application.Ports;

// Story 3.4 review-round-2 patch: `RawDataRowsRead` is distinct from `Readings.Count` so
// ProcessSmartPlugImport can tell "the file's data body had zero rows at all" (corrupt/truncated
// file — FlaggedForReview) apart from "rows were read but every one was at-or-before the
// watermark" (a normal, successful nothing-new incremental re-import — Completed). Counts every
// data-body row the parser iterated over, regardless of whether it parsed successfully or was
// filtered by the watermark.
public sealed record SmartPlugParseResult(IReadOnlyList<SmartPlugReading> Readings, int RawDataRowsRead);

// AD-9: one port, one adapter per vendor (EveHomeXlsxParser, MerossCsvParser) — no vendor-specific
// parsing logic leaks outside the adapter. CanParse lets each adapter own its own filename/
// extension recognition rule (Meross also validates the "Power Monitor Day Data - ..." pattern),
// rather than a switch-on-extension living in the calling use case.
public interface ISmartPlugParser
{
    SmartPlugVendorFormat Vendor { get; }

    bool CanParse(string fileName);

    // Reads only enough of the file to resolve the device/Power Point tag — never touches the
    // data body (Story 3.4 AC #1: the Power Point match/watermark must be resolved "before the
    // data body is read at all"). Meross's tag comes from the filename and doesn't need
    // `fileContent` at all; Eve Home's comes from the file's own header rows.
    string ReadDeviceTag(Stream fileContent, string fileName, CancellationToken cancellationToken = default);

    // Returned readings carry only what the file itself can supply — HouseholdId/
    // SmartPlugImportId/PowerPointId are left at their defaults for the caller
    // (ProcessSmartPlugImport) to fill in once the Household/import/Power-Point-match are known.
    // `watermark == null` means "no prior data for this Power Point (or none known yet)" — parse
    // in full (Story 3.4 AC #4). Non-null means "only rows with IntervalStart strictly greater
    // than watermark belong in the result" — each adapter decides for itself whether that means
    // early-stopping (Eve Home, confirmed newest-first) or filtering every row (Meross, no
    // documented row-order guarantee) — AD-9, never a vendor branch in the caller.
    // An adapter should still check cancellation periodically on a large file so a
    // job-processing shutdown/cancel can interrupt a slow parse instead of blocking the shared
    // dequeue loop until it finishes.
    SmartPlugParseResult Parse(
        Stream fileContent, string fileName, DateTimeOffset? watermark, CancellationToken cancellationToken = default);
}
