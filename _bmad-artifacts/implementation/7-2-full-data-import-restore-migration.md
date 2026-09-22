---
baseline_commit: eac96e37e4c44d982bbdb7bba2c3d4293953991a
---

# Story 7.2: Full Data Import (Restore / Migration)

Status: ready-for-dev

<!-- Note: Validation is optional. Run validate-create-story for quality check before dev-story. -->

## Story

As a Household member,
I want to import a previously exported v2 dataset,
so that I can restore my instance after a disaster or move it to new hosting.

## Acceptance Criteria

1. **Given** a previously exported v2 dataset, **when** I import it, **then** the import validates against the documented v2 export format. [Source: epics/epic-7-data-export-import-disaster-recovery.md#Story 7.2; PRD FR-23]
2. **Given** malformed data in the import file, **when** validation runs, **then** it's rejected and reported with what failed — never partially applied. [Source: epic-7...md#Story 7.2; PRD FR-23; UX-DR14]
3. **Given** the import mechanism, **when** used, **then** it only supports v2-to-v2 restore/migration — it does not read or convert v1 data. [Source: epic-7...md#Story 7.2; PRD FR-23]
4. **Given** a Household that already has data, **when** an import is attempted, **then** it's blocked by default, requiring an explicit "replace all data" confirmation step — there is no partial-merge import mode. [Source: epic-7...md#Story 7.2; PRD FR-23]
5. **Given** a successful "replace all data" import, **when** it completes, **then** it's treated as a wholesale replace, not an edit — it does not go through the `IAuditCorrectionRecorder` mechanism used for regular Meter Reading/Tariff edits. [Source: epic-7...md#Story 7.2; AD-11]
6. **Given** the import surface, **when** reached, **then** it's accessible from Settings. [Source: epic-7...md#Story 7.2; UX-DR12]

## Tasks / Subtasks

- [ ] **Task 1 — Decide and document the import/restore design** (AC: #1, #2, #4, #5, #6; resolves Epic 6 Retro Action Items #3, #4 — see Dev Notes "Kickoff checks (mandatory)")
  - [ ] Decide the upload → validate → confirm → apply flow shape. **Recommendation** (see Dev Notes "Flow design decision"): two phases — (a) a synchronous upload+structural-validation request (fast: JSON parse + `formatVersion` + shape check, no DB write), returning either a reported list of validation failures or an accepted temp-file reference; (b) a second, explicit-confirmation request that enqueues the actual wholesale delete+insert as an **async background job** (AD-6) — the destructive work is exactly the "large bulk write" shape Epic 6 Retro Action Item #3 flags, and must not run synchronously on the request thread (see Dev Notes "Bulk-write lessons from Stories 3.8–3.10").
  - [ ] Decide and **disclose**: does restore always require the "replace all data" confirmation, even for a Household with zero existing rows? **Recommendation:** yes, always — a single code path is simpler and safer than branching on a 12-table "does this Household have any data" check for a UX step that costs nothing when there's nothing to lose.
  - [ ] Decide and **disclose** the destructive-write target: restore always targets the **current authenticated session's Household** (`ICurrentHouseholdAccessor.HouseholdId`) — never creates a new Household, never reuses the import file's original `household.id`. See Dev Notes "Restore write-target design (critical, read first)" — this is the single most important, non-obvious design decision in this story.
  - [ ] Decide and disclose the max upload file size. The 20 MB cap `SmartPlugImportEndpoints.MaxFileSizeBytes` uses does **not** transfer here — Story 7.1's own live-verification export of one real (fairly small) household was already 32 MB. Pick and disclose a larger limit (e.g. 200–500 MB) and confirm it against Kestrel's default max request body size / any ASP.NET Core multipart form-value-count limits (no explicit override exists today for the export endpoint — check `Program.cs` and `web/src/vite.config.ts`'s dev-proxy body-size behavior too).
  - [ ] New doc `docs/data-import-restore.md` (flat layout, matching `docs/local-development.md`/`local-vs-azure-deltas.md`/`self-hosting.md`/`data-export-format.md`): the validate/confirm/apply contract, the structured validation-failure reporting shape, and the restore semantics (what gets deleted, what gets reassigned, what's explicitly out of scope — HouseholdInvite/BackgroundJob/SmartPlugImport/SmartPlugImportGap, unchanged from 7.1's export-scope decision). Update `docs/data-export-format.md`'s own intro sentence ("There is no companion import tool published anywhere yet") once this ships.

- [ ] **Task 2 — Application: import validation** (AC: #1, #2, #3)
  - [ ] New validator (e.g. `EnergyTracker.Application/ValidateHouseholdImport.cs` or folded into the restore use case's own first phase) that deserializes the uploaded JSON against the **same DTO family `ExportHouseholdData.cs` already defines** (`HouseholdExportResult`, `HouseholdSettingsExportDto`, `MeterReadingExportDto`, etc.) — do not invent a second, parallel schema; export and import must read the identical contract.
  - [ ] Reject anything whose `formatVersion` is not exactly `"v2"` (AC #3 — v2-to-v2 only, no v1 read/convert path of any kind).
  - [ ] Collect **every** structural failure before reporting (missing required field, wrong JSON type, an unparseable enum string for `meterRegressionPrompts[].classification`/`statusSnapshots[].status` — these must round-trip against the exact lowercase names `ExportHouseholdData.cs` emits, case-insensitively, and an unrecognized value is a reported failure, never a silent default) — AC #2 requires "rejected and reported with what failed," not fail-fast on the first problem.
  - [ ] No DB write of any kind happens during this validation phase — it is a pure read/parse operation, satisfying "never partially applied" structurally, not by convention.

- [ ] **Task 3 — Application: `RestoreHouseholdData` use case (wholesale delete + insert)** (AC: #1, #4, #5, #6)
  - [ ] New flat file `EnergyTracker.Application/RestoreHouseholdData.cs`, single `ExecuteAsync(Guid householdId, <validated import data>, CancellationToken)` — `householdId` here is always the **current** Household (Task 1's write-target decision), never read from the import file.
  - [ ] Never calls `IAuditCorrectionRecorder` anywhere in this path (AC #5, AD-11's explicit carve-out — this is a wholesale replace, not an edit).
  - [ ] Household's own row is the one exception to "delete then insert": update its settings fields (`Locale`, `Currency`, `YearlyBaselineKwh`, `TrendingThresholdKwh`, `LowConfidenceGapDays`, `TariffCheckCadenceMonths`, `AiPlausibilityEnabled`) in place — never delete/recreate the Household row itself (it's the live tenant root the current request/session is already anchored to). `CreatedAtUtc` and `Id` are never touched. `AiPlausibilityEnabled` is the one Household field imported; `aiPlausibilityBackendConfigured`/`aiPlausibilityBackendLabel` in the file are deployment-level and are never written back (this deployment's own `AiPlausibilityBackendOptions` config is authoritative, matching AD-19).
  - [ ] Every other in-scope entity (HouseholdMember, MainMeter, MeterReading, MeterRegressionPrompt, Tariff, Event, Room, PowerPoint, Device, SmartPlugReading, StatusSnapshot, AuditCorrection) is **fully deleted for the current Household, then the imported rows inserted verbatim** — reusing each row's original `Id` from the file and every one of its *internal* cross-references exactly as exported (`PowerPoint.RoomId`, `Device.PowerPointId`, `MeterReading.MainMeterId`, `MeterRegressionPrompt.MeterReadingId`/`PreviousMeterReadingId`, `SmartPlugReading.PowerPointId`) — the file's internal FK graph is already self-consistent, so no ID remapping is needed for anything except `HouseholdId` itself, which the export file never even carries per row (see `docs/data-export-format.md`'s "Fields deliberately left out" — confirm this while implementing). Set `HouseholdId` explicitly to the current Household's id when constructing each entity.
  - [ ] `SmartPlugReading.SmartPlugImportId` is always `null` on insert (the field isn't in the export at all — `SmartPlugImport` stays out of scope, unchanged from 7.1).
  - [ ] **Verify the actual FK-dependency delete/insert order against this codebase's real `*Configuration.cs` Fluent API files before relying on the ordering below** — it is derived from entity shapes read during story creation, not from an exhaustive FK audit, and a violation would surface as a constraint-violation exception mid-chunk, not a silent bug: **insert order** Household(update) → MainMeter → Room → PowerPoint → Device → MeterReading → MeterRegressionPrompt → HouseholdMember → Tariff → Event → SmartPlugReading → StatusSnapshot → AuditCorrection; **delete order is the reverse**, excluding Household. `Event.TaggedEntityId`/`AuditCorrection.EntityId` are believed to be plain polymorphic `Guid` columns (matching `AuditCorrection.EntityType`'s discriminator-string precedent), not DB-enforced FKs — confirm this too, since a real FK there would change the ordering.
  - [ ] Chunk every delete and every insert per Dev Notes "Bulk-write lessons from Stories 3.8–3.10" (`DeleteBatchSize`-style constant, explicit `CommandTimeout` bump, one outer transaction spanning every chunk — never per-chunk transactions, since "never partially applied" (AC #2's spirit extends to the apply phase too) requires all-or-nothing).
  - [ ] **AD-2 compliance:** plain, portable EF Core LINQ only (`ExecuteDeleteAsync`/`AddRangeAsync`/`SaveChangesAsync`) — the Data Export/Import capability is bound to AD-2/AD-3 only in the capability map, **not** AD-6's bulk-write exception (AD-23). Do **not** reach for `EFCore.BulkExtensions`/`BulkInsertOrUpdateAsync` here: AD-23's provider-branching exception is scoped explicitly and narrowly to `SmartPlugImportRepository.AddAsync` — reusing it for this new path would need its own new AD amendment, which is out of this story's scope. If a plain chunked `AddRangeAsync` loop proves too slow for very large `SmartPlugReading` volumes in practice, that's a disclosed trade-off/future spike (mirrors 7.1's Tier-2-sync disclosure), not something to silently work around by borrowing AD-23's mechanism.

- [ ] **Task 4 — Backend: async job wiring** (AD-6) (AC: #1, #4)
  - [ ] New `JobTypes.RestoreHouseholdData` constant (`EnergyTracker.Application/JobTypes.cs`).
  - [ ] New payload record (e.g. `RestoreHouseholdDataPayload(string TempFilePath, string OriginalFileName)`) mirroring `ProcessSmartPlugImportPayload` exactly — the payload carries only a temp-file path, **never the file bytes** (Azure Storage Queue caps a message at 64 KB; `SmartPlugImportEndpoints.cs:61-66`'s own comment states this constraint).
  - [ ] New `case JobTypes.RestoreHouseholdData:` in `BackgroundJobProcessor.cs`'s switch, following the existing pattern exactly (deserialize payload, resolve `RestoreHouseholdData` use case from the scope, call it).
  - [ ] Define a `HouseholdImportValidationException` (or similar, matching `SmartPlugImportValidationException`'s precedent) for the validation phase if it also runs inside the job — whose `.Message` is safe to forward as `BackgroundJob.ErrorMessage`; any other exception type leaves `ErrorMessage` null so the frontend's own `errorMessage ?? t(...)` fallback renders correctly localized text (`BackgroundJobProcessor.cs`'s round-4-incident-fix convention — do not regress it with a hardcoded English string here).

- [ ] **Task 5 — API: endpoints** (AC: #1, #2, #4, #6)
  - [ ] `POST /api/household-import` (multipart upload, mirrors `SmartPlugImportEndpoints`'s `IFormFile` pattern) — saves the upload to temp disk, runs Task 2's synchronous structural validation. On failure: `400` `ProblemDetails` with a structured list of what failed (AC #2 — "reported with what failed"; follow this codebase's `errorCode`/extension-property convention rather than inventing a new shape). On success: `200`/`202` with a summary (entity counts) and a reference token the client passes to the next call — do not require re-uploading the file for the confirm step. **The token must be an opaque server-generated id (e.g. a new `Guid`), never the raw temp file path** (leaking a server filesystem path to the client is its own disclosure risk); persist the `(token → tempFilePath, HouseholdId)` mapping server-side (DB row or in-memory cache with a short TTL) and have the confirm endpoint verify the token belongs to the **current** `ICurrentHouseholdAccessor.HouseholdId` before enqueueing — otherwise one Household's uploaded-but-not-yet-confirmed import could be confirmed by a guessed/leaked token from a different Household (IDOR).
  - [ ] `POST /api/household-import/{token}/confirm` (or equivalent) — re-validates the token's Household ownership (above), enqueues the `RestoreHouseholdData` async job (Task 4), returns `202` + `jobId`, following `SmartPlugImportEndpoints.cs`'s exact enqueue-then-202 shape (including deleting the temp file on an enqueue failure, matching its `catch { File.Delete(...); throw; }` block).
  - [ ] `GET /api/jobs/{id}` is already generic (`JobEndpoints.cs`) — **no backend change needed there**, matching Story 3.10's own "reuse polling as-is" precedent.
  - [ ] 403 `ProblemDetails` via the same `TryGetHouseholdId` pattern every other endpoint file copies (`HouseholdExportEndpoints.cs`'s own copy is the closest sibling).
  - [ ] Explicit file-size cap (Task 1's decided value) enforced the same way `SmartPlugImportEndpoints.MaxFileSizeBytes` is.

- [ ] **Task 6 — Frontend: Settings import UI** (UX-DR12, UX-DR14) (AC: #4, #6)
  - [ ] New component folder `web/src/components/data-import/` (mirrors `data-export/`), e.g. `data-import-panel.tsx`, wired into `web/src/components/settings/settings-page.tsx` immediately alongside `DataExportPanel` (both under the same "Data export & import" heading area — 7.1 deliberately left the copy general enough for this).
  - [ ] Flow: file picker → upload+validate (`POST /api/household-import`) → **on failure**, render UX-DR14's explicit "Import data fails validation" state (`EXPERIENCE.md`'s State Patterns table: "Malformed data ... is rejected and reported with what failed — never partially applied") listing the reported failures, not a generic error → **on success**, show an explicit confirmation dialog ("this will replace all existing data," AC #4) reusing the `Dialog`/`DialogHeader`/`DialogFooter` pattern `settings-page.tsx`'s own Logoff flow already establishes in this same file → confirm → `POST .../confirm` → poll `GET /api/jobs/{id}` (mirror `use-smart-plug-import-job.ts`'s polling-hook shape: 2s interval, tolerate a few consecutive transient failures, distinguish `queued` from `failed`) → success/failure state.
  - [ ] No dedicated mockup exists for this screen (same as 7.1's own "No UX mockup" precedent — `key-settings.html` only shows a bare row; `EXPERIENCE.md` names the Settings reach point and the validation-failure state but not a layout) — build directly against `GlassCard`/`Button`/`Dialog` conventions.
  - [ ] i18next copy keys under `settings.dataImport.*` in both `web/src/locales/en-US/translation.json` and `web/src/locales/de-DE/translation.json`.

- [ ] **Task 7 — Tests** (all ACs)
  - [ ] `EnergyTracker.Application.Tests`: validation tests covering every collected-failure case (wrong `formatVersion`, missing required field, unparseable enum string) each asserting the **full** failure list, not just the first; a happy-path `RestoreHouseholdDataTests` proving every one of the 12 entity categories round-trips, `IAuditCorrectionRecorder` is never invoked (NSubstitute `.DidNotReceive()`), and internal cross-references (e.g. `PowerPoint.RoomId`) are preserved unchanged from the input.
  - [ ] `EnergyTracker.Infrastructure.Tests`: a **real-DbContext** Testcontainers test (dual-provider, per this codebase's testing rules) proving (a) the delete-then-insert actually respects AD-3/writes the current Household's id onto every row regardless of what the file implies, (b) a restore targeting a Household whose current session's `HouseholdMember` issuer/subject is included in the imported member list still resolves via `CurrentHouseholdAccessor` on the next request (the session-continuity edge case from Dev Notes), and (c) restoring onto a Household with pre-existing data leaves zero pre-existing rows behind afterward (true wholesale replace, not merge).
  - [ ] `EnergyTracker.Api.Tests`: happy path (`202` + poll-to-`completed`); malformed-file `400` with the reported failure list; no-Household `403`; confirm-without-a-prior-validate-token rejected.
  - [ ] Frontend: `data-import-panel.test.tsx` colocated next to the component (Vitest + Testing Library) covering upload → validation-failure display, upload → confirm-dialog → poll → success, and the poll → failure path.

- [ ] **Task 8 — Live Auth0/Chrome verification** (all ACs — does not defer, hard gate per Epic 6 Retrospective Action Item #1)
  - [ ] **This test is genuinely destructive against real data — pause and get Ralf's explicit go-ahead before running it**, unlike 7.1's read-only export verification. Recommended safest live scenario: export the real household's current data (reuse Story 7.1's now-shipped export), then restore that **exact same file back onto the same Household** — a real round trip with no risk of creating an orphaned second Household or losing data.
  - [ ] Confirm live: the validation-failure UI actually renders for a deliberately corrupted file; the confirm dialog appears and blocks on real existing data; after confirming, the job polls to completion and every entity category the export included is present again in the running app; **the currently logged-in session is not broken** (no forced re-login, `Settings` and `Dashboard` still load normally immediately after) — this is the direct verification of the `HouseholdMember` session-continuity design decision in Dev Notes.
  - [ ] **Per Epic 6 Retrospective Action Item #1: if the Claude-in-Chrome extension reports "not connected" at this step, raise it immediately and pause for the user to start/connect Chrome, then resume — do not check this task off on the strength of a passing component/unit test alone.**

## Dev Notes

### Kickoff checks (mandatory — Epic 6 Retrospective Action Items #3 and #4)

The Epic 6 retrospective flagged two items explicitly assigned to story creation for Story 7.2, both resolved by design decisions above, restated here so they aren't lost:

- **Action Item #3** ("apply Stories 3.8–3.10's bulk-write/chunking/command-timeout lessons — a full-household restore is the same 'large bulk write' shape that caused 4 real production incidents"): addressed by Task 3/Task 4's async-job + chunked-transaction design — see "Bulk-write lessons from Stories 3.8–3.10" below.
- **Action Item #4** ("resolve the EF `DateTimeOffset` value-comparer gap ... before it becomes reachable via restore/migration writes"): resolved by design, not by code change — see "Why the DateTimeOffset value-comparer gap does not apply here" below. **Confirm this reasoning holds during implementation**; if the actual implementation ends up doing an in-place `UPDATE` of any existing tracked row's `DateTimeOffset` property to an instant-equal-but-different-offset value anywhere in this path, stop and re-read `spec-datetimeoffset-utc-normalization.md`'s deferred item before proceeding.

### Restore write-target design (critical, read first)

`ICurrentHouseholdAccessor` resolves the request's Household by looking up the authenticated principal's `(ExternalIssuer, ExternalSubjectId)` against the `HouseholdMembers` table (`CurrentHouseholdAccessor.cs`) — **not** by a stored session/cookie Household id. This has a direct consequence for restore:

- Restore must **always** write into the Household the current session already resolves to (`ICurrentHouseholdAccessor.HouseholdId`) — never create a new Household row, never reuse the import file's `household.id` as a real primary key. On "move to new hosting," the very first authenticated visitor already went through FR-26 and got a **freshly created** Household with a new `Id` before they ever reach this Settings screen; restore replaces that fresh Household's *contents*, it does not resurrect the old deployment's Household row under its old id.
- Because `ExportHouseholdData`/`docs/data-export-format.md` deliberately **excludes** `HouseholdId` from every child entity in the file (it's implicit — "the whole document is scoped to exactly one Household"), there is nothing to remap on the way in: every reconstructed entity just gets `HouseholdId = <current Household's id>` set directly, and every entity's *internal* cross-reference (`PowerPoint.RoomId`, `MeterReading.MainMeterId`, etc.) is reused byte-for-byte from the file, since those references were never Household-qualified in the first place and the file's internal graph is already self-consistent.
- `HouseholdMember` rows are deleted and reinserted like everything else. Because `CurrentHouseholdAccessor` resolves by issuer+subject (not by a stable row id), the person performing the restore keeps a working session across the operation **as long as their own `(ExternalIssuer, ExternalSubjectId)` is present among the imported members** — true by construction for the disaster-recovery/self-restore case (same person, same OIDC provider) and for the move-to-new-hosting case (same identity, new deployment). If it is ever *not* present (e.g. restoring someone else's export under a different identity), the current principal loses their Household on the next request and is routed back into FR-26's creation flow — a real, disclosed edge case, not solved by this story, but worth an explicit test (Task 7) proving the *expected* case works, and a code comment noting the edge case exists.
- The restore's DB work runs inside the async background job, which resolves `HouseholdId` from the job envelope (`JobHouseholdContext`), not from an HTTP principal — so the destructive write itself never depends on a live `HouseholdMember` row lookup mid-flight (see `CurrentHouseholdAccessor.Resolve()`'s job-processing branch). Continuity is only a concern for the *next* request after the job completes.

### Bulk-write lessons from Stories 3.8–3.10 (Retro Action Item #3)

`DELETE /api/smart-plug-import-jobs?deleteAll=true` caused **four real production incidents** in Epic 3 before landing on a stable shape — restore's delete-everything-then-insert-everything is structurally the same problem, just across all 12 entity categories instead of one, and adds an insert phase the cleanup story never had. Concrete, proven numbers/patterns to reuse rather than re-derive:

- `SmartPlugImportRepository.DeleteBatchSize = 200` — the one chunk-size value already proven safe against both SQL Server's ~2,100-parameter statement ceiling and per-command log-volume saturation on Azure SQL Basic. Use the same value (or a named constant with the same reasoning) for restore's chunked deletes and inserts, rather than guessing a new number.
- `CommandTimeout` is **per-command, not per-transaction** — `SmartPlugImportRepository` explicitly bumps it (`dbContext.Database.SetCommandTimeout(TimeSpan.FromSeconds(120))`, `180` for a heavier path) before a bulk operation. Chunking bounds each individual command; the explicit bump is still needed because the app-wide default is lower.
- **One outer transaction spans every chunk** (`BeginTransactionAsync`/commit once, loop chunks inside it) — never per-chunk transactions. This is what makes "never partially applied" true for the destructive phase, matching AC #2's spirit even though that AC is worded around the validation phase.
- **Wall-clock ceiling is separate from any single command's timeout.** Story 3.10's round-3 incident happened *after* every individual command was already bounded — total work still exceeded Container Apps' ~240s synchronous HTTP ingress ceiling. This is exactly why Task 1/4 mandate the async-job shape from the start, not as a later fix.
- **A large child-row cascade needs its own explicit chunked detach**, not reliance on an FK's cascade behavior as one unbounded operation (Story 3.10's 4th incident: a `SetNull` cascade alone blew `CommandTimeout` for a 122,158-row detach). Watch for the same shape here: deleting a household's `Room`/`PowerPoint` rows must not implicitly cascade-touch `SmartPlugReading` as one giant operation — `SmartPlugReading` is explicitly deleted as its own chunked step in this story's ordering, before `Room`/`PowerPoint`, for exactly this reason.
- **Explicitly accepted, not mitigated:** SQL Server lock-escalation risk under a large single transaction was raised during the 3.10 incident response and deliberately left unmitigated (`spec-3-10-cleanup-per-import-detach.md`: "a more thorough mitigation ... deserves its own dedicated investigation ... not a same-day addition"). The same acceptance applies here — do not attempt a `LOCK_ESCALATION` schema change as part of this story.

### Why the DateTimeOffset value-comparer gap does not apply here

`spec-datetimeoffset-utc-normalization.md`'s deferred item: EF Core's default `DateTimeOffset` value comparer compares only the UTC instant, so an **`UPDATE`** that reassigns an *already-tracked* row's `DateTimeOffset` property to an instant-equal-but-different-offset value can be silently skipped by change tracking. This restore design never does that:

- Every entity except `Household` is deleted, then **inserted fresh** — an `INSERT` is unaffected by the value comparer (the gap is specifically about `UPDATE`s being skipped as "no-op").
- `Household` **is** updated in place, but none of its updated fields (`Locale`, `Currency`, `YearlyBaselineKwh`, `TrendingThresholdKwh`, `LowConfidenceGapDays`, `TariffCheckCadenceMonths`, `AiPlausibilityEnabled`) is a `DateTimeOffset`. `Household.CreatedAtUtc` (the one `DateTimeOffset` field) is never written on restore.

If implementation ever deviates from this (e.g. an "upsert instead of delete+insert" optimization for performance), re-open this question before shipping it — that is precisely the shape the deferred item warns about.

### Format reuse — do not build a second schema

`docs/data-export-format.md` (Story 7.1) is the authoritative, already-shipped contract. Import's validation and deserialization target must be the **same DTO records** `ExportHouseholdData.cs` already defines (`HouseholdExportResult`, `HouseholdSettingsExportDto`, `HouseholdMemberExportDto`, `MainMeterExportDto`, `MeterReadingExportDto`, `MeterRegressionPromptExportDto`, `TariffExportDto`, `EventExportDto`, `RoomExportDto`, `PowerPointExportDto`, `DeviceExportDto`, `SmartPlugReadingExportDto`, `StatusSnapshotExportDto`, `AuditCorrectionExportDto`) — reusing them (rather than a parallel "import DTO" set) is what guarantees the documented format and the accepted format can never silently drift apart.

### Relevant architecture invariants (do not violate)

- **AD-2 (dual-provider):** plain portable EF Core LINQ only for the delete/insert work — no `EFCore.BulkExtensions`/AD-23 mechanism (see Task 3's explicit carve-out reasoning), no provider-specific SQL.
- **AD-3 (tenant isolation):** every inserted row's `HouseholdId` is set explicitly to the current session's Household id (the DbContext's global query filter governs reads; writes always set the FK explicitly, matching every existing write path in this codebase, e.g. `ProcessSmartPlugImport`).
- **AD-11 (audit correction):** this path never calls `IAuditCorrectionRecorder` — explicit carve-out already named in the AD's own text.
- **AD-10 (historical tag integrity):** restore's delete-then-insert of `Room`/`PowerPoint`/`Device` is a distinct, disclosed exception to FR-28's normal soft-delete-only UX rule — it's a wholesale administrative replace behind an explicit confirmation gate, not a user-facing "delete this Room" action, so AD-10's soft-delete convention doesn't apply to this specific mechanism the way it does to FR-28's own delete endpoint.
- **AD-19 (secrets):** never write `aiPlausibilityBackendConfigured`/`aiPlausibilityBackendLabel` from the import file back into any deployment-level config — those are read-only, informational fields in the export, sourced from this deployment's own `AiPlausibilityBackendOptions` at export time, not settings to restore.
- **AD-6 (async jobs):** payload is a plain JSON-serializable record carrying only a temp-file path, never file bytes (Azure Storage Queue's 64 KB message cap) — mirrors `ProcessSmartPlugImportPayload` exactly.

### Testing standards summary

.NET: `{SubjectClass}Tests` per class, `Snake_case_with_underscores` method names, Shouldly assertions, NSubstitute mocks against ports, `TestContext.Current.CancellationToken` (xUnit v3 MTP). A Testcontainers-backed Infrastructure test is required for the delete/insert-ordering and AD-3-write-target claims — an NSubstitute-mocked Application-layer test cannot verify real FK ordering or that the global query filter still reads consistently afterward. Frontend: Vitest + Testing Library, colocated `*.test.tsx`.

### Project Structure Notes

- Backend: new use case `EnergyTracker.Application/RestoreHouseholdData.cs` and validation logic (Task 2), new port(s)/adapter(s) as needed in `Application/Ports`/`Infrastructure/Adapters` (mirror `IHouseholdExportReader`/`HouseholdExportReader`'s placement); new endpoint file `EnergyTracker.Api/Endpoints/HouseholdImportEndpoints.cs`, registered alongside the other `Map*Endpoints` calls in `Program.cs`; new `JobTypes.RestoreHouseholdData` constant and `BackgroundJobProcessor.cs` switch case.
- New doc: `docs/data-import-restore.md` (flat, same folder as `data-export-format.md`).
- Frontend: new `web/src/components/data-import/` folder, wired into `web/src/components/settings/settings-page.tsx` next to `DataExportPanel`.
- No conflicts detected with existing structure/conventions beyond what's called out above (verify the FK-ordering assumption against real `*Configuration.cs` files per Task 3).

### References

- [Source: _bmad-artifacts/planning/epics/epic-7-data-export-import-disaster-recovery.md#Story 7.2]
- [Source: _bmad-artifacts/planning/prds/prd-energy-tracker-2026-08-08/prd/4-features.md#FR-23]
- [Source: _bmad-artifacts/planning/architecture/architecture-energy-tracker-2026-08-09/ARCHITECTURE-SPINE/invariants-rules.md#AD-2, AD-3, AD-6, AD-10, AD-11, AD-19]
- [Source: _bmad-artifacts/planning/architecture/architecture-energy-tracker-2026-08-09/ARCHITECTURE-SPINE/capability-architecture-map.md — "Data Export/Import (FR-22–FR-23) | Application use case over all repositories | AD-2, AD-3"]
- [Source: _bmad-artifacts/planning/ux-designs/ux-energy-tracker-2026-08-08/EXPERIENCE.md — Settings row (data export/import, FR-22/23), State Patterns table row "Import data fails validation" (UX-DR14)]
- [Source: _bmad-artifacts/planning/epics/requirements-inventory.md#UX-DR12, UX-DR14]
- [Source: _bmad-artifacts/implementation/7-1-full-data-export.md — export format/entity-scope decisions this story must stay consistent with]
- [Source: docs/data-export-format.md — the authoritative v2 format this story validates against and reuses DTOs from]
- [Source: _bmad-artifacts/implementation/epic-6-retro-2026-09-21.md — Action Items #3 (bulk-write lessons) and #4 (DateTimeOffset value-comparer gap), both explicitly assigned to this story's creation]
- [Source: _bmad-artifacts/implementation/spec-datetimeoffset-utc-normalization.md — the deferred value-comparer gap resolved by design in this story's Dev Notes]
- [Source: _bmad-artifacts/implementation/spec-3-10-cleanup-batch-delete-fix.md, spec-3-10-cleanup-per-import-detach.md, spec-3-10-cleanup-async-job.md — the 4 real production incidents and their concrete fixes (DeleteBatchSize=200, CommandTimeout bumps, async-job move, chunked detach-before-delete)]
- [Source: src/EnergyTracker.Application/ExportHouseholdData.cs — the DTO family this story's validation/import must reuse]
- [Source: src/EnergyTracker.Infrastructure/Adapters/HouseholdExportReader.cs — read-path precedent for entity scope/AD-3 handling]
- [Source: src/EnergyTracker.Api/Endpoints/HouseholdExportEndpoints.cs, SmartPlugImportEndpoints.cs — endpoint/TryGetHouseholdId/temp-file-upload patterns to mirror]
- [Source: src/EnergyTracker.Infrastructure/Adapters/BackgroundJobProcessor.cs, src/EnergyTracker.Application/JobTypes.cs, src/EnergyTracker.Application/Ports/IBackgroundJobQueue.cs — AD-6 async job wiring to extend]
- [Source: src/EnergyTracker.Infrastructure/Adapters/CurrentHouseholdAccessor.cs — the issuer/subject lookup behind the session-continuity design decision]
- [Source: src/EnergyTracker.Infrastructure/Adapters/SmartPlugImportRepository.cs — DeleteBatchSize/DeleteReadingVolumeThreshold/CommandTimeout/transaction precedents]
- [Source: web/src/components/data-export/data-export-panel.tsx, web/src/components/smart-plug-import/use-smart-plug-import-job.ts — frontend fetch/ApiError and job-polling patterns to mirror]
- [Source: web/src/components/settings/settings-page.tsx — Settings composition pattern, existing Dialog-based confirmation flow (Logoff) to mirror for the "replace all data" confirmation]
- [Source: _bmad-artifacts/project-context.md]

## Dev Agent Record

### Agent Model Used

### Debug Log References

### Completion Notes List

### File List
