using EnergyTracker.Domain;

namespace EnergyTracker.Application.Ports;

// AD-9: one port, one adapter per vendor (EveHomeXlsxParser, MerossCsvParser) — no vendor-specific
// parsing logic leaks outside the adapter. CanParse lets each adapter own its own filename/
// extension recognition rule (Meross also validates the "Power Monitor Day Data - ..." pattern),
// rather than a switch-on-extension living in the calling use case.
public interface ISmartPlugParser
{
    SmartPlugVendorFormat Vendor { get; }

    bool CanParse(string fileName);

    // Returned readings carry only what the file itself can supply — HouseholdId/
    // SmartPlugImportId/PowerPointId are left at their defaults for the caller
    // (ProcessSmartPlugImport) to fill in once the Household/import/Power-Point-match are known.
    // Defaulted so existing direct-parser callers (tests) don't need to pass one — an adapter
    // should still check it periodically on a large file so a job-processing shutdown/cancel can
    // interrupt a slow parse instead of blocking the shared dequeue loop until it finishes.
    IReadOnlyList<SmartPlugReading> Parse(Stream fileContent, string fileName, CancellationToken cancellationToken = default);
}
