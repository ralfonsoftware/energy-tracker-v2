---
baseline_commit: e03d145860ba65cd59f6400ba0525a28130ba64a
---

# Story 3.2: Import-to-Power-Point Mapping

Status: done

<!-- Note: Validation is optional. Run validate-create-story for quality check before dev-story. -->

## Story

As a Household member,
I want an import tagged to a Power Point that doesn't exist yet to prompt me to create or map it,
so that my data isn't silently dropped or misfiled.

## Acceptance Criteria

1. **Given** an import file tagged (by device name/filename) to a Power Point that doesn't yet exist in my Household, **when** the import is processed, **then** I'm prompted to create it or map it to an existing Power Point, rather than the import silently failing (FR-4).
2. **Given** I create or map the Power Point during this flow, **when** the import completes, **then** the `SmartPlugReading` rows are associated with that Power Point.
3. **Given** the import's Room/Power Point tag, **when** the data is written, **then** the tag identity is snapshotted by value at write time (denormalized display fields) — a later retag of the Power Point's Room does not rewrite this import's historical attribution (AD-10).

**Scope note on AC #1's "Power Point/Device" wording (epic file):** matching stays at **Power Point granularity only** — `SmartPlugReading` has no `DeviceId` column (Story 3.1 deliberately scoped parsing/matching to Power Point level; see `Dev Notes` below and `3-1-smart-plug-file-upload-async-parsing.md`'s Task 3 note "no Device entity is resolved"). Do not add Device-level mapping or a `DeviceId` column — that would be new schema scope this story's ACs don't require.

## Tasks / Subtasks

- [x] **Task 1: Backend — mapping use case & repository extension** (AC: #1, #2, #3)
  - [x] `Application/Ports/ISmartPlugImportRepository.cs`: add `Task<SmartPlugImport?> FindByIdAsync(Guid smartPlugImportId, CancellationToken)`, `Task<IReadOnlyList<SmartPlugReading>> ListReadingsByImportIdAsync(Guid smartPlugImportId, CancellationToken)`, and `Task UpdateMappingAsync(SmartPlugImport import, IReadOnlyList<SmartPlugReading> readings, CancellationToken)`. Implement in `Infrastructure/Adapters/SmartPlugImportRepository.cs` — `UpdateMappingAsync` mirrors `AddAsync`'s single-`SaveChangesAsync` pattern (one transaction; a partially-updated import/readings set must never be observable).
  - [x] New `Application/MapSmartPlugImportToPowerPoint.cs`: `MapSmartPlugImportToPowerPoint(ISmartPlugImportRepository, ITaggingScaffoldRepository)` with `ExecuteAsync(Guid smartPlugImportId, Guid powerPointId, CancellationToken)`. Logic: find the import (404-equivalent if missing — cross-Household lookups already return nothing because `SmartPlugImportRepository`'s queries go through `EnergyTrackerDbContext`'s AD-3 query filter, same as `ITaggingScaffoldRepository`'s Find methods — do **not** add an explicit `householdId` parameter or manual filter); throw if `import.Status != SmartPlugImportStatus.AwaitingPowerPointMapping` (reuse `SmartPlugImportValidationException` from `ProcessSmartPlugImport.cs`, same namespace); find the target Power Point via `ITaggingScaffoldRepository.FindPowerPointAsync` (throw existing `TaggingScaffoldNotFoundException` if missing, existing `TaggingScaffoldParentArchivedException` if `ArchivedAt is not null` — reuse both, don't invent new ones); resolve the Power Point's Room via `FindRoomAsync` for its display name; load the import's readings via `ListReadingsByImportIdAsync` and set `PowerPointId`/`PowerPointName`/`RoomName` on every one (AD-10 — this mapping call **is** "write time" for these previously-unattributed readings, since they were persisted with `PowerPointId = null` back in Story 3.1's `ProcessSmartPlugImport`); set `import.Status = SmartPlugImportStatus.Completed`; persist via `UpdateMappingAsync`. Add `SmartPlugImportNotFoundException(Guid id)` in the same file (mirrors `TaggingScaffoldNotFoundException`'s one-line shape).
  - [x] **Do not call `IStatusRecomputeService` here.** Same AD-7 boundary Story 3.1 respected for `ProcessSmartPlugImport` — Story 3.3 owns wiring recompute-on-import-completion. This use case introduces a **second** path by which a `SmartPlugImport` reaches `Completed` (besides 3.1's direct-match path); leave a note for 3.3's author (see Dev Notes) that both paths need the hook, not just `ProcessSmartPlugImport`.

- [x] **Task 2: Backend — API surface** (AC: #1, #2)
  - [x] `Api/Endpoints/JobEndpoints.cs`: extend `JobStatusResponse` with a new `Guid? SmartPlugImportId` field (last position) and `GetBackgroundJobStatus`'s `BackgroundJobStatusResult` record with the same — `GetBackgroundJobStatus.ExecuteAsync` already loads the `SmartPlugImport` row when `JobType == JobTypes.ProcessSmartPlugImport`; just also carry `import?.Id` through so the frontend can address the new mapping endpoint from a polled job-status response (today it has no way to learn the `SmartPlugImportId` at all).
  - [x] `Api/Endpoints/SmartPlugImportEndpoints.cs`: add `POST /api/smart-plug-imports/{id}/power-point-mapping` (`id` = `SmartPlugImportId`) calling `MapSmartPlugImportToPowerPoint`, in the same `MapSmartPlugImportEndpoints` group as the existing upload endpoint (one file per feature-endpoint-group, matching `TaggingScaffoldEndpoints.cs`'s precedent). Request: `record MapSmartPlugImportRequest(Guid PowerPointId)`. Response: `record SmartPlugImportMappingResponse(Guid Id, string Status)` (`Status` lowercased via `.ToString().ToLowerInvariant()`, matching `JobStatusResponse`'s convention). Error mapping: `SmartPlugImportNotFoundException` → 404, `TaggingScaffoldNotFoundException` → 404, `SmartPlugImportValidationException` (wrong state) → 409, `TaggingScaffoldParentArchivedException` (target Power Point archived) → 409. No `TryGetHouseholdId`/403 guard needed beyond what the AD-3 query filter already enforces at the repository layer (mirrors `TaggingScaffoldEndpoints`' rename/move handlers, which also skip an explicit household check since Find-then-mutate already can't cross a boundary).
  - [x] **Reuse the existing `POST /api/power-points` endpoint unchanged** for the "create a new Power Point" branch of the flow — do not add a second Power-Point-creation code path. The frontend calls it first, then calls the new mapping endpoint with the resulting id (two sequential calls, not one atomic endpoint — see Dev Notes for why this is an acceptable, recoverable two-step design in this codebase).
  - [x] No new `GET` list endpoint for pending/awaiting-mapping imports — out of scope (see Dev Notes' "Known non-goals").

- [x] **Task 3: Frontend — mapping prompt UI** (AC: #1, #2)
  - [x] `web/src/lib/smart-plug-import-api.ts`: add `smartPlugImportId: string | null` to `JobStatusDto`; add `mapSmartPlugImportToPowerPoint(smartPlugImportId: string, powerPointId: string): Promise<void>` (`POST /api/smart-plug-imports/{id}/power-point-mapping`); add `fetchRooms(): Promise<RoomDto[]>`, `fetchPowerPoints(): Promise<PowerPointDto[]>`, `createPowerPoint(roomId: string, name: string): Promise<PowerPointDto>` (`GET /api/rooms`, `GET /api/power-points`, `POST /api/power-points`) — all following this file's existing `ApiError`/`toApiError` shape, **not** `tagging-scaffold-manager.tsx`'s inline raw-`fetch` pattern (no shared tagging-scaffold API client file exists in this codebase; adding these here keeps this feature's API calls typed and error-handled consistently with the rest of this file rather than duplicating the untyped inline pattern). `RoomDto`/`PowerPointDto` shapes: `{ id: string; name: string; archivedAt: string | null }` / `{ id: string; roomId: string; name: string; archivedAt: string | null }` (exact fields `tagging-scaffold-manager.tsx` already uses — camelCase, matches ASP.NET Core's default JSON casing). Also added `smartPlugImportDeviceTag: string | null` to `JobStatusDto` (and `JobStatusResponse`/`BackgroundJobStatusResult` on the backend) — the mockup's dialog title/create-button prefill need the parsed device tag, which wasn't otherwise reachable by the frontend; `GetBackgroundJobStatus` already loads the `SmartPlugImport` row for `importStatus`, so carrying `DeviceTag` through costs nothing extra.
  - [x] New `web/src/components/smart-plug-import/power-point-mapping-dialog.tsx`: modal using `Dialog`/`DialogContent`/`DialogHeader`/`DialogTitle` from `@/components/ui/dialog` + `GLASS_MODAL_CLASSNAME` from `@/lib/glass-classnames` (exact pattern `tagging-scaffold-manager.tsx` already uses for its dialogs). Renders mockup **State 3** ([mockups/key-smart-plug-import.html:409-449](../planning/ux-designs/ux-energy-tracker-2026-08-08/mockups/key-smart-plug-import.html)): title `New Power Point: "{deviceTag}"`, body copy, primary "Create Power Point" action, "or map to an existing one" divider, list of existing (non-archived) Power Points as tappable rows labeled `{roomName} → {powerPointName}` (fetch via `fetchRooms`/`fetchPowerPoints`, join client-side).
    - **The mockup's create button has no Room picker, but `PowerPoint.RoomId` is non-nullable** — added a minimal Room selector (a native `<select>` populated from `fetchRooms()`, defaulting to the first non-archived Room) as a small, deliberate addition beyond the literal mock, noted in a code comment.
    - Existing-Power-Point row tap → `mapSmartPlugImportToPowerPoint(smartPlugImportId, powerPointId)` directly.
    - Create-and-map path → `createPowerPoint(selectedRoomId, deviceTag)` (name pre-filled to the parsed `deviceTag`, editable) then `mapSmartPlugImportToPowerPoint(smartPlugImportId, newPowerPoint.id)`. `ApiError` (e.g. a duplicate name in that Room from `CreatePowerPoint`'s existing validation) surfaces inline in the dialog rather than closing it silently.
    - On mapping success: calls the `onMapped` prop so the parent can flip to the completed state.
  - [x] `web/src/components/smart-plug-import/smart-plug-import-panel.tsx`: tracks `smartPlugImportId`/`deviceTag` (refs alongside `jobIdRef`, captured from `job.smartPlugImportId`/`job.smartPlugImportDeviceTag` when polling resolves to `awaitingpowerpointmapping`). Replaced the `awaitingMappingNote` placeholder paragraph with `<PowerPointMappingDialog>` rendered when `state === 'awaitingMapping'`, wired to flip `state` to `'completed'` on success.
  - [x] i18n: added `smartPlugImport.mappingModal.*` keys to both `web/src/locales/en-US/translation.json` and `web/src/locales/de-DE/translation.json` (title, body, createButton, roomLabel, orDivider, noExisting, mapError). Removed `smartPlugImport.awaitingMappingNote` from both locale files (its "Mapping support is coming soon" copy is now dead — the real dialog replaces the paragraph that rendered it) rather than leaving a stale unused key.

- [x] **Task 4: Tests** (AC: all)
  - [x] `tests/EnergyTracker.Application.Tests/MapSmartPlugImportToPowerPointTests.cs` (NSubstitute mocks of `ISmartPlugImportRepository`/`ITaggingScaffoldRepository`, Shouldly, `Snake_case` method names per project-context.md): happy path sets every reading's `PowerPointId`/`PowerPointName`/`RoomName` and the import's `Status` to `Completed`; throws `SmartPlugImportNotFoundException` when the import id doesn't resolve; throws `SmartPlugImportValidationException` when `import.Status != AwaitingPowerPointMapping`; throws `TaggingScaffoldNotFoundException` when the Power Point id doesn't resolve; throws `TaggingScaffoldParentArchivedException` when the Power Point is archived.
  - [x] `tests/EnergyTracker.Api.Tests/SmartPlugImportEndpointsTests.cs` (extend; Testcontainers real Postgres, matching Story 3.1's precedent): end-to-end — upload a file whose device tag matches no existing Power Point → poll `GET /api/jobs/{id}` to `awaitingpowerpointmapping` (asserting the response now carries a non-null `smartPlugImportId`) → `POST /api/smart-plug-imports/{id}/power-point-mapping` to an existing Power Point → verify readings' `PowerPointId` set and `GET /api/jobs/{id}` now reports `completed`; mapping a cross-Household `SmartPlugImportId` → 404 (AD-3 IDOR guard, mirrors the existing pattern); mapping to an archived Power Point → 409; mapping an already-`Completed` import a second time → 409.
  - [x] Frontend Vitest (colocated, `@testing-library/react`, `jsdom`): `power-point-mapping-dialog.test.tsx` — create-new-Power-Point path (incl. the Room-picker requirement and a duplicate-name error surfaced inline), map-to-existing-Power-Point path, error handling. Extended `smart-plug-import-panel.test.tsx` for the dialog now rendering in the `awaitingMapping` state. Extended `smart-plug-import-api.test.ts` for `mapSmartPlugImportToPowerPoint`/`fetchRooms`/`fetchPowerPoints`/`createPowerPoint`.
  - [x] Confirm `tests/EnergyTracker.Architecture.Tests/PatternDetectiveDoesNotReferenceSmartPlugOrEventDataTests.cs` still passes untouched — this story doesn't touch any of its 9 guarded files.

### Review Findings

- [x] [Review][Patch] New Power Point created after a failed mapping call never appears in the dialog's "map to existing" list [web/src/components/smart-plug-import/power-point-mapping-dialog.tsx:44-101] — `rooms`/`powerPoints` are fetched once in a `useEffect` with an empty dependency array; `handleCreateAndMap`'s catch block never refetches or appends the newly-created Power Point. This directly contradicts the Dev Notes' documented recovery guarantee ("the user can immediately retry via the same dialog's 'map to an existing one' list, which will now include the just-created Power Point") and, combined with the story's own "no page-reload persistence" non-goal, can leave an import permanently stuck in `AwaitingPowerPointMapping` with no path forward in the UI.
- [x] [Review][Patch] Dialog's close (X) button renders but is non-functional; a load failure has no retry or escape [web/src/components/smart-plug-import/power-point-mapping-dialog.tsx:108,176] — `DialogContent` always renders a visible Close (X) button via Radix, but `<Dialog open>` passes no `onOpenChange`, so the X button, Escape, and overlay-click all silently no-op (the `open` prop is hardcoded `true`). Combined with the `loadError` branch offering no retry action, a transient failure of `/api/rooms` or `/api/power-points` permanently traps the user in the dialog short of a full page reload (which loses in-flight import tracking per the story's own non-goals).
- [x] [Review][Patch] New mapping endpoint omits the `TryGetHouseholdId`/403 guard every sibling endpoint uses [src/EnergyTracker.Api/Endpoints/SmartPlugImportEndpoints.cs:103-134] — `RenamePowerPoint`/`MovePowerPoint` in `TaggingScaffoldEndpoints.cs` (the exact precedent this story's Dev Notes cite) still call `TryGetHouseholdId` for the 403 guard even though they discard the id. Without it here, an authenticated principal with no Household row gets a misleading 404 "SmartPlugImport not found" instead of the codebase-standard 403 "does not belong to a Household."
- [x] [Review][Patch] Mapping-completed imports never get `CompletedAtUtc` set, unlike `ProcessSmartPlugImport`'s direct-match path [src/EnergyTracker.Application/MapSmartPlugImportToPowerPoint.cs:43] — `import.Status = SmartPlugImportStatus.Completed` is set without `import.CompletedAtUtc = DateTimeOffset.UtcNow` (compare `ProcessSmartPlugImport.cs:83,138`), leaving a data-integrity gap between the two completion paths. Currently unsurfaced by any API response, but a real inconsistency in persisted data.
- [x] [Review][Patch] `TaggingScaffoldParentArchivedException` reused outside its documented contract [src/EnergyTracker.Application/MapSmartPlugImportToPowerPoint.cs:25-28] — the exception's own XML doc scopes it to "creating a Power Point under an archived Room, or a Device under an archived Power Point" (a parent-archived-on-create case). Here it's thrown for the target Power Point's own archived state — a different case that only produces a correct-looking message/409 by coincidence of the current constructor text, not by the exception's contract.
- [x] [Review][Patch] `UpdateMappingAsync` forces full-column UPDATEs on already-tracked entities [src/EnergyTracker.Infrastructure/Adapters/SmartPlugImportRepository.cs:420-431] — `import`/`readings` are already tracked by the same scoped `DbContext` (loaded via `FindByIdAsync`/`ListReadingsByImportIdAsync` earlier in the same request); calling `.Update()`/`.UpdateRange()` on already-tracked entities marks every property Modified instead of letting EF's change tracker diff only the touched columns.
- [x] [Review][Patch] Create-Power-Point button disables silently with no explanation when the Household has zero active Rooms [web/src/components/smart-plug-import/power-point-mapping-dialog.tsx:120-150] — `activeRooms.length === 0` disables the Room `<select>` and, via an empty `selectedRoomId`, the Create button too, but no message tells the user why they can't create a Power Point.
- [x] [Review][Patch] Task 4 parent checkbox left unchecked despite all subtasks and deliverables complete [_bmad-artifacts/implementation/3-2-import-to-power-point-mapping.md:48] — all four subtasks are `[x]` and are in fact delivered in the diff; the parent checkbox is stale.
- [x] [Review][Defer] No optimistic-concurrency protection on SmartPlugImport mapping — concurrent/double-submit requests can race [src/EnergyTracker.Application/MapSmartPlugImportToPowerPoint.cs:16-45] — deferred, pre-existing: no Application-layer use case in this codebase carries a concurrency token beyond `Household`/`HouseholdInvite`; fixing this is a broader architectural decision, not specific to this diff.
- [x] [Review][Defer] Mapping endpoint isn't idempotent — retrying after a lost response on a successful mapping returns 409 instead of the original success [src/EnergyTracker.Application/MapSmartPlugImportToPowerPoint.cs:16-20] — deferred, pre-existing: no idempotency-key pattern exists anywhere in this codebase.
- [x] [Review][Defer] `ListReadingsByImportIdAsync`/`UpdateMappingAsync` load and update an import's full reading set unpaged [src/EnergyTracker.Infrastructure/Adapters/SmartPlugImportRepository.cs:415-431] — deferred, pre-existing: mirrors `ProcessSmartPlugImport`'s existing bulk-write pattern from Story 3.1, not introduced by this diff.

## Dev Notes

### This story completes a state Story 3.1 deliberately parked, not started

Story 3.1's `ProcessSmartPlugImport` already persists an unmatched import as `SmartPlugImport.Status = AwaitingPowerPointMapping` with its `SmartPlugReading` rows written at `PowerPointId = null` (display fields `RoomName`/`PowerPointName` populated from the file itself — Eve Home's `Raum:`/`Gerät:` headers, or empty `RoomName` + filename-derived tag for Meross). 3.1's frontend panel already transitions to an `awaitingMapping` UI state on poll — it currently just shows a static badge + placeholder note ("Mapping support is coming soon"). **This story's entire job is: (a) a backend way to resolve that parked state, and (b) replacing the placeholder note with the real create/map prompt.** No new parsing, upload, or polling infrastructure is needed — all of it already exists from 3.1.

### Architecture constraints (binding, not optional)

- **AD-10 (Historical tag integrity):** `SmartPlugReading.PowerPointId`/`PowerPointName`/`RoomName` are snapshotted **by value** at the moment they're first attached to a Power Point — for a mapped-late import, that moment is *this story's* mapping call, not the original parse. A later re-parenting of that Power Point's Room must not retroactively rewrite this import's attribution (same rule Story 3.1 already implements for the direct-match path).
- **AD-3 (Tenant isolation):** every repository lookup this story adds (`FindByIdAsync`, `ListReadingsByImportIdAsync`) relies on `EnergyTrackerDbContext`'s global query filter, sourced from the HTTP-resolved `ICurrentHouseholdAccessor` — this is an HTTP-endpoint-driven use case (not job-context like `ProcessSmartPlugImport`), so it follows `RenamePowerPoint`/`MovePowerPoint`'s pattern: no explicit `householdId` parameter, no manual filter, a cross-Household id simply resolves to nothing → typed not-found exception → 404. Do **not** copy `ProcessSmartPlugImport`'s job-context `householdId`-parameter shape here; that shape exists specifically because job processing has no HTTP principal to resolve from.
- **AD-7 boundary — do not cross it:** exactly like 3.1, this story never calls `IStatusRecomputeService`. It does, however, introduce a **second code path** (besides `ProcessSmartPlugImport`'s direct match) by which a `SmartPlugImport.Status` reaches `Completed`. **Flag for Story 3.3's author:** when 3.3 wires import-completion → `IStatusRecomputeService`, it must hook both `ProcessSmartPlugImport`'s direct-match completion *and* `MapSmartPlugImportToPowerPoint`'s mapping-completion — missing the second path would mean Status silently never sharpens for any household that ever needed the create/map prompt.
- **AD-9 boundary:** matching/mapping stays at Power Point granularity — see the Acceptance Criteria section's scope note above. Do not add a `DeviceId` column to `SmartPlugReading` or attempt Device-level resolution; that's schema scope beyond this story's ACs.

### Existing code to reuse, not reinvent

- `CreatePowerPoint` (`src/EnergyTracker.Application/CreatePowerPoint.cs`) and its endpoint (`POST /api/power-points` in `TaggingScaffoldEndpoints.cs`) are used **unchanged** for the "create new Power Point" branch — already validates name uniqueness within the Room and rejects an archived parent Room (`TaggingScaffoldValidationException`/`TaggingScaffoldParentArchivedException`). The frontend's create-and-map flow is two sequential calls (create, then map), not one atomic backend operation.
  - **Why two calls is acceptable here:** if the create call succeeds but the mapping call then fails (network blip, etc.), the Power Point now exists but the import stays `AwaitingPowerPointMapping` — the user can immediately retry via the same dialog's "map to an existing one" list, which will now include the just-created Power Point. No data is lost or silently dropped either way, satisfying the epic's "isn't silently dropped or misfiled" framing without needing a cross-use-case transaction this codebase has no existing precedent for.
- `TaggingScaffoldNotFoundException` / `TaggingScaffoldParentArchivedException` (`src/EnergyTracker.Application/`) are reused for the target-Power-Point checks in `MapSmartPlugImportToPowerPoint` — do not define parallel exception types for the same concepts.
- `SmartPlugImportValidationException` (already defined at the bottom of `ProcessSmartPlugImport.cs`) is reused for the "import not in `AwaitingPowerPointMapping` state" check — same exception, different call site/HTTP mapping (409, not the job-processor's implicit `Failed`-recording path).

### UX / accessibility

- Mockup State 3 ([mockups/key-smart-plug-import.html:409-449](../planning/ux-designs/ux-energy-tracker-2026-08-08/mockups/key-smart-plug-import.html)) is the literal reference — neutral glass-dialog language (not destructive/red), reusing the same modal pattern as the Meter Regression prompt (`key-log-reading-flow.html`), since an unmatched tag is an expected step, not an error.
- The mockup's own dev-handoff comment (`key-smart-plug-import.html:243-244`) flags that the dimmed backdrop content must be `inert` (or `aria-hidden` + focus trap) while the modal is open — shadcn's `Dialog` (Radix-based) already handles this; don't build a custom backdrop that skips it.
- No status-triad color reuse — same DESIGN.md discipline Story 3.1's rubric review already enforced on this screen (`review-rubric.md:37`); the mockup's own CSS comments (`key-smart-plug-import.html:155-163`, `194-200`) document exactly which colors were already corrected away from status semantics — mirror that restraint for any new UI here (use standard shadcn `Button`/`Dialog`/`Input` variants, no custom status-triad colors).

### Known non-goals (avoid scope creep)

- **No page-reload persistence/recovery** of the mapping prompt. `smart-plug-import-panel.tsx`'s `state`/`fileName`/`jobIdRef` are pure React component state today — a reload loses in-flight tracking for *every* terminal state (`completed`/`failed`/`awaitingMapping` alike), not just the mapping prompt. This is a pre-existing characteristic from Story 3.1, not something this story regresses or is required to fix. Do not build a `GET /api/smart-plug-imports` list endpoint or any "pending imports" persistence to work around it.
- **No Device-level mapping** — see the Acceptance Criteria scope note.

### Project Structure Notes

- Backend new files: `src/EnergyTracker.Application/MapSmartPlugImportToPowerPoint.cs` (includes `SmartPlugImportNotFoundException`).
- Backend modified files: `src/EnergyTracker.Application/Ports/ISmartPlugImportRepository.cs`, `src/EnergyTracker.Infrastructure/Adapters/SmartPlugImportRepository.cs`, `src/EnergyTracker.Application/GetBackgroundJobStatus.cs`, `src/EnergyTracker.Api/Endpoints/JobEndpoints.cs`, `src/EnergyTracker.Api/Endpoints/SmartPlugImportEndpoints.cs`.
- **No EF migration needed** — no schema changes. `SmartPlugReading.PowerPointId`/`PowerPointName`/`RoomName` already exist as mutable (`set`, not `init`) fields from Story 3.1, precisely so a later mapping call like this one's could fill them in.
- Frontend new files: `web/src/components/smart-plug-import/power-point-mapping-dialog.tsx` (+ colocated `power-point-mapping-dialog.test.tsx`).
- Frontend modified files: `web/src/components/smart-plug-import/smart-plug-import-panel.tsx` (+ its colocated test), `web/src/lib/smart-plug-import-api.ts` (+ its colocated test), `web/src/locales/en-US/translation.json`, `web/src/locales/de-DE/translation.json`.
- Fits the existing flat, one-class-per-file convention (project-context.md) and the established `web/src/components/smart-plug-import/` grouping from Story 3.1 — no new top-level folders.

### Testing standards summary

- .NET: xUnit v3 MTP, Shouldly assertions, NSubstitute mocks against ports, `TestContext.Current.CancellationToken`, Testcontainers (real Postgres) for Api-level tests. One test class per subject, `Snake_case_with_underscores` method names.
- Frontend: Vitest + Testing Library, colocated next to source, `jsdom` environment.

### References

- [Source: _bmad-artifacts/planning/epics/epic-3-smart-plug-import-baseline-sharpening.md#Story 3.2] — story ACs, epic framing.
- [Source: _bmad-artifacts/implementation/3-1-smart-plug-file-upload-async-parsing.md] — the exact `AwaitingPowerPointMapping` state, entity shapes, and existing endpoints/components this story extends; Task 3's "no Device entity is resolved" scoping note; the Dev Notes' AD-6/AD-3/AD-9/AD-10/AD-7 constraints, still binding here.
- [Source: src/EnergyTracker.Application/ProcessSmartPlugImport.cs] — how an import currently reaches `AwaitingPowerPointMapping`; `SmartPlugImportValidationException` reused by this story.
- [Source: src/EnergyTracker.Application/Ports/ISmartPlugImportRepository.cs, src/EnergyTracker.Infrastructure/Adapters/SmartPlugImportRepository.cs] — repository shape/pattern to extend (`AddAsync`'s single-`SaveChangesAsync` precedent for `UpdateMappingAsync`).
- [Source: src/EnergyTracker.Application/RenamePowerPoint.cs, MovePowerPoint.cs] — the Find-then-mutate, no-explicit-`householdId` pattern this story's new use case follows (HTTP-context, not job-context).
- [Source: src/EnergyTracker.Application/CreatePowerPoint.cs, src/EnergyTracker.Api/Endpoints/TaggingScaffoldEndpoints.cs] — reused unchanged for the create-Power-Point branch.
- [Source: src/EnergyTracker.Api/Endpoints/JobEndpoints.cs, src/EnergyTracker.Application/GetBackgroundJobStatus.cs] — extended to carry `SmartPlugImportId` through the poll response.
- [Source: web/src/components/smart-plug-import/smart-plug-import-panel.tsx, web/src/lib/smart-plug-import-api.ts] — the exact files/patterns (`ApiError`/`toApiError`, polling state machine) this story extends.
- [Source: web/src/components/tagging-scaffold/tagging-scaffold-manager.tsx] — `Dialog`/`GLASS_MODAL_CLASSNAME` modal pattern and `RoomDto`/`PowerPointDto` field shapes to mirror; confirms no shared tagging-scaffold API client file exists.
- [Source: _bmad-artifacts/planning/architecture/architecture-energy-tracker-2026-08-09/ARCHITECTURE-SPINE/invariants-rules.md#AD-3, AD-7, AD-9, AD-10] — binding rules.
- [Source: _bmad-artifacts/planning/ux-designs/ux-energy-tracker-2026-08-08/mockups/key-smart-plug-import.html:235-262, 409-449] — State 3 visual reference, modal a11y dev-handoff note.
- [Source: _bmad-artifacts/planning/ux-designs/ux-energy-tracker-2026-08-08/DESIGN/dos-and-donts.md, review-rubric.md:37] — status-triad color-reuse prohibition, already enforced once on this exact screen.
- [Source: web/src/locales/en-US/translation.json, web/src/locales/de-DE/translation.json#smartPlugImport] — existing keys to extend; the stale "coming soon" `awaitingMappingNote` copy to replace.
- [Source: _bmad-artifacts/project-context.md] — project-wide coding/testing conventions.

## Dev Agent Record

### Agent Model Used

Claude Sonnet 5 (claude-sonnet-5)

### Debug Log References

None — no blocking failures encountered during implementation.

### Completion Notes List

- Backend: `MapSmartPlugImportToPowerPoint` use case added with `ISmartPlugImportRepository` extensions (`FindByIdAsync`, `ListReadingsByImportIdAsync`, `UpdateMappingAsync`). Reuses `TaggingScaffoldNotFoundException`/`TaggingScaffoldParentArchivedException`/`SmartPlugImportValidationException` per Dev Notes — no new exception types beyond `SmartPlugImportNotFoundException`. AD-3 (no explicit householdId param), AD-7 (no `IStatusRecomputeService` call), AD-9 (Power-Point-only granularity), AD-10 (by-value snapshot at mapping time) all respected.
- New endpoint `POST /api/smart-plug-imports/{id}/power-point-mapping` added to the existing `MapSmartPlugImportEndpoints` group; error mapping 404/404/409/409 per Dev Notes. `POST /api/power-points` reused unchanged for the create-Power-Point branch.
- `JobStatusResponse`/`BackgroundJobStatusResponse` extended with `SmartPlugImportId` (per Task 2) **and** `SmartPlugImportDeviceTag` (a necessary addition beyond Task 2's literal scope — the mapping dialog's title and create-button prefill need the parsed device tag, which was otherwise unreachable by the frontend; `GetBackgroundJobStatus` already loads the `SmartPlugImport` row for `importStatus`, so this costs nothing extra).
- Frontend: `power-point-mapping-dialog.tsx` renders mockup State 3 (title/body/create-button/divider/existing-Power-Point list), with a minimal native Room `<select>` added beyond the literal mock since `PowerPoint.RoomId` is non-nullable. `smart-plug-import-panel.tsx` now renders the dialog in the `awaitingMapping` state instead of the stale "coming soon" placeholder, which was removed from both locale files.
- Followed TDD throughout: wrote failing tests first for the Application-layer use case, the Api-level endpoint (Testcontainers), and both frontend test files, then implemented until green.
- **Live-verified in a real browser** (Chrome via Claude-in-Chrome) against the real local dev stack (dotnet API + Vite + real Postgres, not mocks): uploaded a real Eve Home sample export with an unmatched device tag → dialog appeared with the correct parsed device tag and Room picker → created a new Power Point and confirmed the import completed with all readings correctly attributed (verified directly in Postgres) → uploaded a second unmatched file and mapped it to the just-created existing Power Point via the "map to an existing one" list → confirmed both imports completed and all readings share the correct `PowerPointId`/`PowerPointName`/`RoomName` (AD-10).
- Full regression suite green: 148 Application + 106 Api.Tests (Testcontainers Postgres) + 3 Architecture.Tests backend tests; 131 frontend Vitest tests (16 new: 7 dialog, 2 panel, 9 api). `dotnet build` clean (0 warnings beyond pre-existing SSH.NET advisory), `tsc -b`/`oxlint`/`vite build` clean.

### File List

**Backend — new:**
- `src/EnergyTracker.Application/MapSmartPlugImportToPowerPoint.cs`
- `tests/EnergyTracker.Application.Tests/MapSmartPlugImportToPowerPointTests.cs`

**Backend — modified:**
- `src/EnergyTracker.Application/Ports/ISmartPlugImportRepository.cs`
- `src/EnergyTracker.Infrastructure/Adapters/SmartPlugImportRepository.cs`
- `src/EnergyTracker.Application/GetBackgroundJobStatus.cs`
- `src/EnergyTracker.Api/Endpoints/JobEndpoints.cs`
- `src/EnergyTracker.Api/Endpoints/SmartPlugImportEndpoints.cs`
- `src/EnergyTracker.Api/Program.cs` (DI registration for `MapSmartPlugImportToPowerPoint`)
- `tests/EnergyTracker.Api.Tests/SmartPlugImportEndpointsTests.cs`

**Frontend — new:**
- `web/src/components/smart-plug-import/power-point-mapping-dialog.tsx`
- `web/src/components/smart-plug-import/power-point-mapping-dialog.test.tsx`

**Frontend — modified:**
- `web/src/lib/smart-plug-import-api.ts`
- `web/src/lib/smart-plug-import-api.test.ts`
- `web/src/components/smart-plug-import/smart-plug-import-panel.tsx`
- `web/src/components/smart-plug-import/smart-plug-import-panel.test.tsx`
- `web/src/locales/en-US/translation.json`
- `web/src/locales/de-DE/translation.json`
