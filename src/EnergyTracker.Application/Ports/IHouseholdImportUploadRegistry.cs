namespace EnergyTracker.Application.Ports;

public record HouseholdImportUploadReference(Guid HouseholdId, string TempFilePath, string OriginalFileName);

// Task 5's opaque-token requirement: POST /api/household-import returns a server-generated Guid
// token, never the raw temp file path, and the confirm endpoint must verify the token belongs to
// the CURRENT Household before enqueueing the restore job (otherwise a guessed/leaked token from a
// different Household could confirm someone else's upload — IDOR). Register/Consume is this
// port's whole contract; there is no separate "peek without consuming" method because nothing in
// this flow needs one.
public interface IHouseholdImportUploadRegistry
{
    // Story 11.10 (audit S4): at most one pending upload per Household. Registering replaces the
    // Household's previous, still-registered upload and deletes its file; a token that was already
    // consumed (its file now belongs to a restore job) is never touched.
    void Register(Guid token, HouseholdImportUploadReference reference);

    // Atomically looks up AND removes the entry (never a separate check-then-remove) so a token
    // can only ever be confirmed once, and returns null both when the token doesn't exist and when
    // it belongs to a different Household — the caller (the confirm endpoint) reports the same 404
    // either way, never distinguishing "not found" from "not yours" (AD-3 IDOR-guard convention).
    // An expired or wrong-Household entry is removed too, and its file is deleted.
    HouseholdImportUploadReference? Consume(Guid token, Guid householdId);
}
