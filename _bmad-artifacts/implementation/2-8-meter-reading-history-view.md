---
baseline_commit: 14c9452e8c5ced5de3db3c2ffbdaa1098f796e00
---

# Story 2.8: Meter Reading History View

Status: done

<!-- Note: Validation is optional. Run validate-create-story for quality check before dev-story. -->

## Story

As a Household member,
I want a dedicated, browsable list of every Meter Reading I've logged,
so that I can find and correct a specific past entry, distinct from just seeing the aggregate trend.

## Acceptance Criteria

1. **Given** the Meter Reading History view, **when** I open it, **then** it lists individual Meter Readings (value + timestamp) for the Main Meter, ordered by timestamp — not entry order, consistent with FR-1/FR-25's sequencing (FR-31).
2. **Given** Trend History (FR-8) and Status Calculation Detail (FR-30), **when** comparing surfaces, **then** this view is the only place raw, per-Reading data is browsable — both of those stay aggregate-only, unchanged by this story (FR-31, FR-8, FR-30).
3. **Given** a Reading in the list, **when** I open it to correct a mis-logged value, **then** editing preserves the original value as a visible correction note rather than a silent overwrite — this story is the first to wire the shared audit-trail mechanism (NFR8) into a Meter Reading edit path (FR-31, NFR8).
4. **Given** a Reading currently under an open, unconfirmed regression classification (Story 2.3 / FR-25), **when** it appears in the list, **then** it's visibly flagged as pending rather than shown as a normal confirmed entry (FR-31, FR-25).
5. **Given** the existing `/api/meter-readings` POST endpoint, **when** the history list is served, **then** it's exposed via a new paginated GET on the same route, following the codebase's existing kebab-case-plural route convention (FR-31, AD-consistency-conventions).

## Confirmed with Ralf during story creation (read before assuming a gap was missed)

FR-31 was added to Epic 2 on 2026-08-23, **after** the UX design pass (`ux-energy-tracker-2026-08-08/`) was frozen — same situation Story 2.7 documented for FR-30. There is no mockup, no `UX-DR*` citation, and — unlike Story 2.7's Status Detail dialog — **no nav-chrome slot either**: `nav-chrome.tsx`'s bottom tab bar has exactly 4 fixed entries (Dashboard, Trend History, Tariff Radar, Settings) per UX-DR9, and the middle two are still inert placeholders reserved for Epic 4/Epic 5 — this story must not repurpose or add a 5th tab. `EXPERIENCE.md`'s own nav-pattern note (line 22) says less-frequent surfaces are "reached through Settings or contextual entry points rather than claiming their own tab" — this story is exactly that kind of surface.

Since this genuinely wasn't resolvable from existing docs, it was confirmed directly with Ralf before writing tasks:

- **Surface shape:** a new full page (like `SettingsPage`), not a dialog. A dialog (Story 2.7's `StatusDetailDialog` pattern) was considered and rejected — a paginated, editable list doesn't fit a dialog well.
- **Entry point:** a text-link trigger on the Dashboard, placed the same way Story 2.7's `detailTrigger` is (a small underlined link, not an icon) — mirroring the precedent that already exists on this exact surface, not the Settings page.
- **Local `view` state, not a route:** `App.tsx` already uses local `view: 'dashboard' | 'settings'` state (no `react-router`, per Story 1.5's deferral). This story adds a third value, `'history'`, the same way.

## Tasks / Subtasks

### Backend

- [x] **Task 1 — Add `MeterReading.Version` (AD-4) and the `AuditCorrection` table (AD-11) (AC #3)**
  - [x] `src/EnergyTracker.Domain/MeterReading.cs`: add `public int Version { get; set; }`. **Delete** the file's existing top-of-file comment explaining why there's deliberately no Version column yet ("Deliberately no Version/concurrency-token column here (AD-4)... Add it only when a story actually implements edits") — this is that story. Mirror `Household.Version`'s exact shape (`src/EnergyTracker.Domain/Household.cs:35`), not a new pattern.
  - [x] `src/EnergyTracker.Infrastructure/Configurations/MeterReadingConfiguration.cs`: add `builder.Property(r => r.Version).IsConcurrencyToken();` — identical syntax to `HouseholdConfiguration.cs:42-43`.
  - [x] New file `src/EnergyTracker.Domain/AuditCorrection.cs` — AD-11's shared table, first consumer. Fields: `Id` (Guid), `HouseholdId` (Guid, AD-3), `EntityType` (string — `"MeterReading"` for this story; a plain discriminator string, not an enum, so a future entity type like `"Tariff"` is a data addition, not a schema change), `EntityId` (Guid), `FieldName` (string — `"KwhValue"` for this story), `OldValue` (string), `NewValue` (string), `CorrectedAtUtc` (DateTimeOffset). **`OldValue`/`NewValue` are stored via `decimal.ToString(CultureInfo.InvariantCulture)`, never the ambient/household locale** — AD-18 explicitly requires storage to stay locale-neutral regardless of `Locale`; only display formatting is locale-aware.
  - [x] New file `src/EnergyTracker.Infrastructure/Configurations/AuditCorrectionConfiguration.cs`: `ToTable("AuditCorrections")`, `HasKey(a => a.Id)`, `HasMaxLength` on the 4 string columns (e.g. 64 for `EntityType`/`FieldName`, 256 for `OldValue`/`NewValue`), `HasIndex(a => a.HouseholdId)` (AD-3 — every filtered query needs this), `HasIndex(a => new { a.EntityType, a.EntityId })` (the lookup path Task 4's batch query needs). **No FK from `EntityId` to `MeterReading`** — `EntityId` is polymorphic across future entity types (Tariff, per AD-11's own binding list), so a real FK constraint can only ever target one table; this is a deliberate omission, not a missed constraint.
  - [x] `src/EnergyTracker.Infrastructure/EnergyTrackerDbContext.cs`: add `public DbSet<AuditCorrection> AuditCorrections => Set<AuditCorrection>();` and `modelBuilder.Entity<AuditCorrection>().HasQueryFilter(e => e.HouseholdId == CurrentHouseholdId);` — same line-for-line pattern as every other Household-scoped entity in this file (lines 59-69).
  - [x] Add the migration via `scripts/add-migration.sh AddMeterReadingVersionAndAuditCorrection` — **never** `dotnet ef migrations add` directly (AD-2). This single migration covers both the new `Version` column on `MeterReadings` and the new `AuditCorrections` table; it must land in both `EnergyTracker.Infrastructure.Migrations.Postgres` and `.SqlServer` in the same commit.

- [x] **Task 2 — `IAuditCorrectionRecorder` port + adapter (AC #3)**
  - [x] New file `src/EnergyTracker.Application/Ports/IAuditCorrectionRecorder.cs`:
    ```csharp
    public interface IAuditCorrectionRecorder
    {
        Task RecordAsync(Guid householdId, string entityType, Guid entityId, string fieldName, string oldValue, string newValue, CancellationToken cancellationToken);

        // Latest correction per entity id (greatest CorrectedAtUtc), keyed by EntityId. A row
        // corrected more than once accumulates multiple AuditCorrection rows — full history is
        // preserved in the table — but only the most recent is surfaced as the visible "corrected
        // from X" note (NFR8); no AC in this story requires a full audit-log view.
        Task<IReadOnlyDictionary<Guid, AuditCorrection>> GetLatestForEntitiesAsync(string entityType, IReadOnlyList<Guid> entityIds, CancellationToken cancellationToken);
    }
    ```
  - [x] New file `src/EnergyTracker.Infrastructure/Adapters/AuditCorrectionRecorder.cs` implementing the above against `EnergyTrackerDbContext.AuditCorrections`. `GetLatestForEntitiesAsync` groups by `EntityId`, takes the row with the max `CorrectedAtUtc` per group — do this with a single query (`GroupBy` + `OrderByDescending`/`First` translated server-side), not an N+1 loop over `entityIds`.
  - [x] `src/EnergyTracker.Api/Program.cs`: `builder.Services.AddScoped<IAuditCorrectionRecorder, AuditCorrectionRecorder>();` — add near the existing `IMeterReadingRepository`/`IMeterRegressionPromptRepository` registrations (`Program.cs:297-299`).

- [x] **Task 3 — Extract shared kWh validation from `CreateMeterReading` (AC #3)**
  - [x] New file `src/EnergyTracker.Application/MeterReadingValidation.cs`: `internal static class MeterReadingValidation` exposing `public const decimal MaxKwhValue = 1_000_000_000_000_000m;` and `public static void ValidateKwhValue(decimal kwhValue)` throwing `MeterReadingValidationException` on the exact same `kwhValue <= 0 || kwhValue >= MaxKwhValue` condition and message currently inline in `CreateMeterReading.cs:16,48-52`. This is the exact same value/message — a pure extraction, not a behavior change.
  - [x] `src/EnergyTracker.Application/CreateMeterReading.cs`: replace its private `MaxKwhValue` const and inline validation block with a call to `MeterReadingValidation.ValidateKwhValue(kwhValue)`. Task 5's `EditMeterReading` reuses the same call — without this extraction, the two use cases would silently drift on their value bound the way Story 2.7's Task 4 existed specifically to prevent for the pace/baseline difference sign logic.
  - [x] `tests/EnergyTracker.Application.Tests/CreateMeterReadingTests.cs`: no behavior change expected — existing bound-validation test cases must still pass unmodified (confirms the extraction is behavior-preserving).

- [x] **Task 4 — Repository extensions: paginated read + concurrency-safe update (AC #1, #3, #5)**
  - [x] `src/EnergyTracker.Application/Ports/IMeterReadingRepository.cs`: add two methods.
    - `Task<(IReadOnlyList<MeterReading> Items, int TotalCount)> GetPageForMainMeterAsync(Guid mainMeterId, int page, int pageSize, CancellationToken cancellationToken)` — ordered by `ReadingTimestamp` **descending**, then `Id` descending as the deterministic tiebreak (mirrors `FindImmediatelyPrecedingAsync`'s existing tiebreak pattern). Descending (most-recent-first) is this story's own explicit choice for a browsable history list — a household member correcting a recent mis-entry shouldn't have to page to the end; nothing in FR-31/AC #1 mandates a direction, only that the sort key is timestamp, not entry order.
    - `Task<MeterReading> UpdateKwhValueAsync(Guid readingId, decimal kwhValue, int expectedVersion, CancellationToken cancellationToken)` — throws `MeterReadingConcurrencyConflictException` (new, Task 5) on a version mismatch. Copy `HouseholdRepository.UpdateYearlyBaselineAsync`'s exact shape (`src/EnergyTracker.Infrastructure/Adapters/HouseholdRepository.cs:97-122`): load the entity, set `dbContext.Entry(reading).Property(r => r.Version).OriginalValue = expectedVersion`, mutate `KwhValue`, `reading.Version++`, `SaveChangesAsync`, catch `DbUpdateConcurrencyException` → throw the typed exception. **Do not invent a new concurrency pattern** — this is the second use of an already-established one.
  - [x] `src/EnergyTracker.Infrastructure/Adapters/MeterReadingRepository.cs`: implement both.

- [x] **Task 5 — `GetMeterReadingHistory` use case (AC #1, #4, #5)**
  - [x] New exception `src/EnergyTracker.Application/MeterReadingNotFoundException.cs` (mirrors `MeterRegressionPromptNotFoundException`'s shape).
  - [x] New exception `src/EnergyTracker.Application/MeterReadingConcurrencyConflictException.cs` (mirrors `HouseholdConcurrencyConflictException`'s shape — message-only, no server-state payload, matching the established 409 precedent noted in Task 6).
  - [x] New file `src/EnergyTracker.Application/GetMeterReadingHistory.cs`. Result records:
    ```csharp
    public record MeterReadingHistoryPage(IReadOnlyList<MeterReadingHistoryEntry> Items, int TotalCount, int Page, int PageSize);
    public record MeterReadingHistoryEntry(MeterReading Reading, bool IsPendingRegression, AuditCorrection? LatestCorrection);
    ```
    `ExecuteAsync(Guid householdId, int page, int pageSize, CancellationToken cancellationToken)`:
    1. Validate `page >= 1` and `1 <= pageSize <= 100`, else throw `MeterReadingValidationException` (reuse — this is still a Meter Reading history validation failure, not a new bounded context).
    2. `var mainMeter = await readingRepository.FindMainMeterByHouseholdAsync(householdId, cancellationToken);` — **use the read-only lookup, not `GetOrCreateMainMeterAsync`** (viewing history must never have the side effect of creating a Main Meter for a Household that has never logged a reading). If `null`, return `new MeterReadingHistoryPage([], 0, page, pageSize)` — the empty-history case, not an error (mirrors `GetCurrentStatus`'s undefined-Status-is-not-an-error precedent).
    3. `var (items, totalCount) = await readingRepository.GetPageForMainMeterAsync(mainMeter.Id, page, pageSize, cancellationToken);`
    4. `var openPrompt = await regressionPromptRepository.GetOpenForHouseholdAsync(householdId, cancellationToken);` — AD-12 guarantees at most one open prompt per Main Meter, so "pending" is just `reading.Id == openPrompt?.MeterReadingId` per item — no per-row query needed.
    5. `var corrections = await auditCorrectionRecorder.GetLatestForEntitiesAsync("MeterReading", items.Select(r => r.Id).ToList(), cancellationToken);` — one batch call for the whole page, not N+1.
    6. Map each `MeterReading` into a `MeterReadingHistoryEntry`, return the page.

- [x] **Task 6 — `EditMeterReading` use case (AC #3)**
  - [x] New file `src/EnergyTracker.Application/EditMeterReading.cs`. `ExecuteAsync(Guid householdId, Guid readingId, decimal kwhValue, int expectedVersion, CancellationToken cancellationToken)`:
    1. `MeterReadingValidation.ValidateKwhValue(kwhValue)` (Task 3).
    2. `var reading = await readingRepository.FindByIdAsync(readingId, cancellationToken);` — AD-3's query filter already scopes this to the caller's Household transparently (same as every other Household-scoped read in this codebase); if `null`, throw `MeterReadingNotFoundException(readingId)` → the endpoint maps this to 404. **Do not add a manual `HouseholdId` check** — that would be exactly the per-handler filtering AD-3 exists to prevent.
    3. Capture `var oldValue = reading.KwhValue;` **before** calling update.
    4. `var updated = await readingRepository.UpdateKwhValueAsync(readingId, kwhValue, expectedVersion, cancellationToken);` — propagates `MeterReadingConcurrencyConflictException` on a version mismatch.
    5. **Only if `oldValue != kwhValue`**, record the correction: `await auditCorrectionRecorder.RecordAsync(householdId, "MeterReading", readingId, "KwhValue", oldValue.ToString(CultureInfo.InvariantCulture), kwhValue.ToString(CultureInfo.InvariantCulture), cancellationToken);` — recorded **after** the update succeeds (never before an update that might still conflict), and never for a no-op save of the same value (that isn't a correction).
    6. Return `updated`.
  - [x] **Deliberately out of scope — do not add either of these without a new AC:**
    - **No Status recompute on edit.** AD-7 binds exactly two call sites (`CreateMeterReading`, Smart-Plug-import-completion) to `IStatusRecomputeService`; `deferred-work.md` already documents that even *resolving* a regression prompt was deliberately kept out of that binding ("in-spec per AD-7's two-call-site rule; revisit once FR-8 Trend History is built"). This story follows the same precedent — editing a past reading does not call `IStatusRecomputeService.RecomputeAsync`. The **live** `/api/status` figure is unaffected either way (it's always computed fresh from current data, AD-7), so this only means the persisted `StatusSnapshot` audit trail won't reflect an edit's effect on history until the next natural recompute trigger — a known, precedented gap, not a bug to fix here. Add a `deferred-work.md` entry noting this alongside the existing regression-resolve one.
    - **No interaction with `MeterRegressionPrompt`.** Editing a Reading that currently has an open regression prompt (AC #4's "pending" flag) is allowed — this story does not re-run regression detection on edit, nor block editing a pending Reading, nor resolve/cancel the open prompt as a side effect. No AC requires any of these; inventing one risks conflicting with Story 2.3's own AD-12 invariant (at most one open prompt, resolved only via `ResolveMeterRegressionPrompt`).

- [x] **Task 7 — Endpoints: paginated GET + PUT edit (AC #1, #3, #4, #5)**
  - [x] `src/EnergyTracker.Api/Endpoints/MeterReadingEndpoints.cs` (existing file, additive — same precedent as Story 2.7's Task 2 for `StatusEndpoints.cs`):
    - `api.MapGet("/meter-readings", ...)` with `int page = 1, int pageSize = 20` query params (`[AsParameters]` or plain query-bound ints per Minimal API convention already used elsewhere in this file), the existing `TryGetHouseholdId` guard, calling `GetMeterReadingHistory`. Catch `MeterReadingValidationException` → 400. Map the result to `MeterReadingHistoryPageResponse` (below).
    - `api.MapPut("/meter-readings/{id:guid}", ...)` taking `EditMeterReadingRequest(decimal KwhValue, int Version)`, the same `TryGetHouseholdId` guard, calling `EditMeterReading`. Catch `MeterReadingValidationException` → 400, `MeterReadingNotFoundException` → 404, `MeterReadingConcurrencyConflictException` → 409 (`Results.Problem(detail: ex.Message, statusCode: StatusCodes.Status409Conflict)` — **message only, matching `HouseholdEndpoints.cs`'s established 409 precedent**, not the full server state AD-4's prose literally suggests; the frontend's own refetch covers getting the current value, exactly as `HouseholdEndpoints.cs:117-120` documents for the identical situation).
  - [x] Extend `MeterReadingResponse` with `int Version` (additive — existing `POST /meter-readings` callers unaffected, mirrors Story 2.7's additive-field precedent). Add:
    ```csharp
    public record MeterReadingHistoryPageResponse(IReadOnlyList<MeterReadingHistoryItemResponse> Items, int TotalCount, int Page, int PageSize);
    public record MeterReadingHistoryItemResponse(Guid Id, decimal KwhValue, DateTimeOffset ReadingTimestamp, int Version, bool IsPendingRegression, decimal? CorrectedFromKwhValue, DateTimeOffset? CorrectedAtUtc);
    public record EditMeterReadingRequest(decimal KwhValue, int Version);
    ```
    `CorrectedFromKwhValue`/`CorrectedAtUtc` come from `MeterReadingHistoryEntry.LatestCorrection` — `decimal.Parse(correction.OldValue, CultureInfo.InvariantCulture)` (the inverse of Task 6's `.ToString(CultureInfo.InvariantCulture)`), both `null` when there's no correction.
  - [x] `src/EnergyTracker.Api/Program.cs`: `builder.Services.AddScoped<GetMeterReadingHistory>();` and `builder.Services.AddScoped<EditMeterReading>();` near the existing `CreateMeterReading` registration (`Program.cs:301`).

### Frontend

- [x] **Task 8 — Install shadcn `Table` (AC #1)**
  - [x] `npx shadcn add table` from `web/` — `DESIGN/components.md` explicitly lists `Table` among the standard shadcn components "used unmodified" for exactly this kind of surface; no bespoke list/grid markup.

- [x] **Task 9 — API client (AC #1, #3, #4)**
  - [x] New file `web/src/lib/meter-reading-history-api.ts`. Replicate `status-api.ts`'s `ApiError`/`toApiError` pattern verbatim (same file family, same precedent Story 2.7's Task 3 already followed for a sibling endpoint). Export:
    - `interface MeterReadingHistoryItemDto { id: string; kwhValue: number; readingTimestamp: string; version: number; isPendingRegression: boolean; correctedFromKwhValue: number | null; correctedAtUtc: string | null }`
    - `interface MeterReadingHistoryPageDto { items: MeterReadingHistoryItemDto[]; totalCount: number; page: number; pageSize: number }`
    - `async function fetchMeterReadingHistory(page: number, pageSize: number): Promise<MeterReadingHistoryPageDto>` — `GET /api/meter-readings?page=…&pageSize=…`.
    - `async function updateMeterReading(id: string, kwhValue: number, version: number): Promise<MeterReadingHistoryItemDto>` — `PUT /api/meter-readings/{id}`. On a non-2xx response, throw `ApiError` exactly like every other client in this file family — the 409 case is not special-cased here, the calling component (Task 11) handles it.
  - [x] New file `web/src/lib/meter-reading-history-api.test.ts` — success parsing, `ApiError` on non-2xx, for both functions.

- [x] **Task 10 — `MeterReadingHistoryPage` (AC #1, #2, #4)**
  - [x] New file `web/src/components/meter-reading/meter-reading-history-page.tsx` (existing `meter-reading` folder — same feature domain as `log-reading-sheet.tsx`/`meter-regression-prompt-dialog.tsx`, not a new top-level folder). Shell mirrors `SettingsPage` exactly: `<main className="flex min-h-svh flex-col gap-6 p-4">`, a heading + "Back" button row (`onBack` prop, same shape as `SettingsPageProps.onBack`). **Do not render `NavChrome`** — this page has no tab (see the "Confirmed with Ralf" section above); `NavChrome`'s `active` prop type doesn't even have a value for it, and adding one would mean either inventing a 5th tab (rejected) or misrepresenting one of the 4 existing tabs as active while on an unrelated page.
  - [x] Props: `{ locale: string; onBack: () => void }`. Local state: `page` (starts at 1), `pageSize` (fixed at 20, no user-facing control — no AC asks for a page-size picker), `data: MeterReadingHistoryPageDto | null`, `loading`, `error`. Fetch on mount and whenever `page` changes.
  - [x] Render via the shadcn `Table` (Task 8): columns for kWh value (`Intl.NumberFormat(locale, ...)`, tabular-nums — same discipline as `status-detail-dialog.tsx`), timestamp (`Intl.DateTimeFormat(locale, ...)`), a "Pending" `Badge` (`variant="outline"`, matching `gap-card.tsx`'s existing Badge-for-flagged-state precedent) rendered only when `item.isPendingRegression`, a correction note (`item.correctedFromKwhValue !== null` → a small muted line, e.g. "Originally logged as {{value}} kWh") below the value, and an "Edit" trigger opening Task 11's dialog for that row.
  - [x] Empty state (`data.totalCount === 0`, e.g. a Household that has never logged a reading): a plain text line, no table shell — mirrors the Dashboard's own onboarding-empty treatment in spirit, not a copy of its markup (this page has no Status to be undefined; it's simply "no readings yet").
  - [x] Pagination: plain "Previous"/"Next" `Button`s (`variant="outline"`) either side of a "Page X of Y" label (`Math.ceil(data.totalCount / data.pageSize)`), disabled at the first/last page — no shadcn Pagination primitive; this codebase has no existing pagination UI to match, and two plain buttons is the simplest thing that satisfies AC #1's "browsable list" without inventing new component surface.
  - [x] After a successful edit (Task 11's `onSaved` callback), re-fetch the **current** page (not reset to page 1) — the edited row's Version/correction-note fields must reflect the save, and the household member shouldn't lose their place in a long history.

- [x] **Task 11 — `EditMeterReadingDialog` (AC #3)**
  - [x] New file `web/src/components/meter-reading/edit-meter-reading-dialog.tsx`. Reuse the `Dialog` + `GLASS_MODAL_CLASSNAME` shell (`web/src/lib/glass-classnames.ts`) — same precedent Story 2.7's `StatusDetailDialog` and the existing `MeterRegressionPromptDialog`/`tagging-scaffold-manager.tsx` dialogs already establish. Props: `{ reading: MeterReadingHistoryItemDto; open: boolean; onOpenChange: (open: boolean) => void; onSaved: () => void }`.
  - [x] Body: a single `UnitInput` (`@/components/ui/unit-input`, `unit="kWh"`, `type="number"`, `inputMode="decimal"`, `step="0.01"`, `min="0.01"`) pre-filled with `reading.kwhValue`, mirroring `log-reading-sheet.tsx`'s exact kWh-field setup — **no `ReadingTimestamp` field**: this story scopes editing to `KwhValue` only (correcting "a mis-logged value", per the story statement's own wording); the timestamp isn't part of any AC here and editing it would need its own regression/ordering re-validation this story doesn't cover.
  - [x] On submit: `updateMeterReading(reading.id, Number(kwhValue), reading.version)` (Task 9). On success: close, call `onSaved()`. On a thrown `ApiError` with status 409: show an inline message (e.g. "This reading was changed elsewhere — refresh and try again") and **do not** auto-retry with a bumped version — matches the `HouseholdEndpoints.cs`/`SetYearlyBaseline` precedent of surfacing the conflict and letting the next fetch (Task 10's re-fetch-after-save, or the household member re-opening the row) supply the current value. On any other error, the existing `err instanceof ApiError && err.detail` fallback pattern from `log-reading-sheet.tsx:85`.

- [x] **Task 12 — Wire the Dashboard trigger + `App.tsx` `'history'` view (AC #1)**
  - [x] `web/src/components/dashboard/dashboard-page.tsx`: add an `onHistoryClick: () => void` prop. Render a second small underlined text-link trigger — same classnames/placement pattern as the existing `detailTrigger`'s inner `<button>` (`dashboard-page.tsx:96-103`) — **but a real navigation trigger** (calls `onHistoryClick`, doesn't open a dialog), placed near it (e.g. directly below, inside the populated branch only — same `showPopulated` guard the Status Detail trigger already uses, since there's nothing meaningful to browse in the onboarding-empty state either).
  - [x] `web/src/App.tsx`: extend `view` to `'dashboard' | 'settings' | 'history'`. Render `<MeterReadingHistoryPage locale={state.household.locale} onBack={() => setView('dashboard')} />` when `view === 'history'`. Pass `onHistoryClick={() => setView('history')}` into `DashboardPage`. **Do not add `react-router`** — this is the same local-state pattern `'settings'` already uses (Story 1.5's deferral, `App.tsx:33-37`'s own comment), a third value is additive, not a new pattern.

- [x] **Task 13 — i18n copy (AD-18)**
  - [x] Add a `meterReadingHistory` top-level namespace (sibling to `dashboard`, `meterReading`, `meterRegression`) to both `web/src/locales/en-US/translation.json` and `de-DE/translation.json`: `heading` (e.g. en: `"Meter Reading History"`, de: `"Zählerstandsverlauf"`), `backToApp` (reuse `settings.backToApp`'s exact copy if generic enough — check its literal string first per Story 2.7's Task 7 precedent — else a parallel key), `emptyState` (e.g. en: `"No Meter Readings logged yet."`), `valueColumn`, `timestampColumn`, `pendingBadge` (e.g. en: `"Pending"`), `correctedFrom` (e.g. en: `"Originally logged as {{kwh}} kWh"`), `editTrigger` (e.g. en: `"Edit"`), `editDialogTitle`, `save`, `saving`, `cancel`, `conflictError` (e.g. en: `"This reading was changed elsewhere — refresh and try again."`), `errorGeneric` (reuse `meterReading.errorGeneric` if the exact same copy fits), `loadError`, `previousPage`, `nextPage`, `pageIndicator` (e.g. en: `"Page {{page}} of {{totalPages}}"`).
  - [x] `dashboard` namespace: add one new trigger key (e.g. `dashboard.historyTrigger`: en `"View reading history"`, de analogous) — sibling to the existing `dashboard.statusDetail.trigger` key, not nested under `statusDetail` (this is a separate trigger to a separate surface).
  - [x] Both locale files updated together, verify byte-for-byte key-set parity (Story 2.5/2.6/2.7's established discipline — Story 2.7 verified this via a script; reuse the same check).

### Tests

- [x] **Task 14 — Backend tests**
  - [x] `tests/EnergyTracker.Application.Tests/GetMeterReadingHistoryTests.cs` (new): empty history (no Main Meter yet) returns `TotalCount: 0`; ordering is `ReadingTimestamp` descending; pagination math (`Page`/`PageSize`/`TotalCount` correctness across 2+ pages); the pending-flag case (a reading matching the open prompt's `MeterReadingId` is flagged, others aren't); the correction-note case (a reading with a recorded `AuditCorrection` surfaces its `OldValue`, one without doesn't); invalid `page`/`pageSize` (0, negative, `pageSize` > 100) throw `MeterReadingValidationException`. Use NSubstitute for `IMeterReadingRepository`/`IMeterRegressionPromptRepository`/`IAuditCorrectionRecorder`, following `GetOpenMeterRegressionPromptTests.cs`'s existing mocking style.
  - [x] `tests/EnergyTracker.Application.Tests/EditMeterReadingTests.cs` (new): a valid edit updates `KwhValue` and increments `Version`; an out-of-range value throws `MeterReadingValidationException` (reusing the shared bound from Task 3 — assert the exact same bound `CreateMeterReadingTests.cs` already asserts, to catch drift); editing a non-existent/foreign-household reading throws `MeterReadingNotFoundException`; a stale `Version` throws `MeterReadingConcurrencyConflictException` (mock the repository to throw it); `IAuditCorrectionRecorder.RecordAsync` is called exactly once with the correct old/new values on a real change, and **never called** when the submitted value equals the existing value (assert via NSubstitute `.DidNotReceive()`).
  - [x] `tests/EnergyTracker.Api.Tests/MeterReadingEndpointsTests.cs` (extend, existing file — mirrors this file's existing `POST_meter_readings_...` naming style):
    - `GET_meter_readings_returns_a_paginated_page_ordered_by_timestamp_descending`
    - `GET_meter_readings_reflects_the_pending_flag_for_a_reading_under_an_open_regression_prompt` — create a regression scenario (reuse this test class's or `MeterRegressionPromptEndpointsTests.cs`'s existing lower-reading-triggers-a-prompt fixture pattern), assert exactly one item has `IsPendingRegression: true`.
    - `PUT_meter_readings_id_edits_the_value_and_records_a_correction_note_visible_on_the_next_GET` — the full round-trip AC #3 needs: edit via PUT, then GET the history page and assert `CorrectedFromKwhValue`/`CorrectedAtUtc` are populated with the pre-edit value.
    - `PUT_meter_readings_id_with_a_stale_Version_returns_409`
    - `PUT_meter_readings_id_for_a_reading_that_does_not_exist_returns_404`
    - `PUT_meter_readings_id_with_an_out_of_range_kwhValue_returns_400`
    - Cross-Household isolation: `A_households_meter_reading_history_is_never_affected_by_another_households_readings` (reuse this class's `CreateHouseholdAsync` helper twice, same symmetric-assertion discipline Story 2.7's review added — assert **both** directions, not just one).
    - `A_principal_without_a_Household_is_forbidden_from_...` for both new routes (mirrors the existing `A_principal_without_a_Household_is_forbidden_from_logging_a_reading` case exactly).

- [x] **Task 15 — Frontend tests**
  - [x] `web/src/lib/meter-reading-history-api.test.ts` (Task 9) — see above.
  - [x] `web/src/components/meter-reading/meter-reading-history-page.test.tsx` (new): renders a fetched page of readings; empty state when `totalCount: 0`; Pending badge renders only for `isPendingRegression: true` rows; correction note renders only when `correctedFromKwhValue` is non-null; Previous/Next buttons disabled at the first/last page respectively and advance/retreat `page` correctly; a fetch failure renders an error state (mirrors `status-detail-dialog.test.tsx`'s error-state case).
  - [x] `web/src/components/meter-reading/edit-meter-reading-dialog.test.tsx` (new): submitting a new value calls `updateMeterReading` with the row's current `version`; success calls `onSaved` and closes; a 409 (`ApiError` with that status) shows the conflict message and does **not** call `onSaved`; a non-409 error shows the generic error message.
  - [x] `web/src/components/dashboard/dashboard-page.test.tsx` (extend): the history trigger renders only when `showPopulated` (mirrors the existing `detailTrigger`-only-when-populated assertion) and calls `onHistoryClick` when clicked.
  - [x] `web/src/App.test.tsx` if one exists, else skip — extend with a case for the `'history'` view rendering `MeterReadingHistoryPage` and `onBack` returning to `'dashboard'` (check whether `App.tsx` currently has direct test coverage before adding a new test file for it; `SettingsPage`'s equivalent `'settings'`-view wiring may already be covered — or may not be, follow whatever precedent exists rather than assuming).

- [x] **Task 16 — Documentation**
  - [x] `_bmad-artifacts/implementation/deferred-work.md`: add one entry noting Meter Reading edits don't trigger a Status recompute/snapshot write (Task 6's explicit scope decision), alongside the existing, analogous regression-prompt-resolve entry.
  - [x] No `docs/*.md` changes expected beyond that (no new operator-facing config, adapter, or env var) — confirm once implementation is done, per Story 2.5/2.6/2.7's identical Task 9/Task 9/Task 9 conclusion.

### Review Findings

- [x] [Review][Defer] Epic 4's Story 4.3 "Correcting a Meter Reading" is now materially superseded/contradicted by this story — needs a call on how epic-4 should be reconciled. — `_bmad-artifacts/planning/epics/epic-4-trend-history-per-plug-insight.md:66-90` (Story 4.3) still describes editing a Meter Reading via Trend History (Story 4.1) and requires `IStatusRecomputeService` to recompute Status forward from the corrected reading through to the present, updating the affected `StatusSnapshot` rows, on every edit. Story 2.8 instead built a separate dedicated History page (AC #2 keeps Trend History aggregate-only, per the "Confirmed with Ralf" section) and deliberately does **not** recompute Status on edit — the new `deferred-work.md` entry frames this as "in-spec per AD-7's two-call-site rule" without acknowledging that Story 4.3 explicitly promises exactly this behavior elsewhere in the backlog. Options: (a) mark epic-4's Story 4.3 as superseded/done and fold its remaining AC (Status recompute-forward) into a new follow-up story under Epic 2 or 4; (b) rewrite Story 4.3 to reflect that per-Reading editing now lives on the History page, keeping only the recompute-forward AC; (c) treat the recompute-forward behavior as still-owed future work and cross-reference Story 4.3 from the `deferred-work.md` entry instead of citing only the `ResolveMeterRegressionPrompt` precedent. — **Resolved at the Epic 3 Retro (2026-08-23, `_bmad-artifacts/implementation/epic-3-retro-2026-08-23.md`, Significant Discovery + Action Item #4):** consolidate rather than duplicate — Story 4.1 absorbs this story's browsable-list surface (the Dashboard "History" text-link is removed once 4.1 ships), Story 4.3 keeps its ACs as-is but now explicitly reuses this story's already-built edit-in-place/AD-4/AD-11 mechanics, with the recompute-forward `IStatusRecomputeService` wiring as the only net-new piece. Epic 4's definition (`epic-4-trend-history-per-plug-insight.md`) was updated accordingly.
- [x] [Review][Patch] No-op edit (saving the same kWh value) still bumps `MeterReading.Version` and writes to the DB, causing spurious 409s for anyone else viewing the same reading [src/EnergyTracker.Application/EditMeterReading.cs:25] — fixed: `ExecuteAsync` now returns the unchanged `reading` immediately when `oldValue == kwhValue`, before any repository write.
- [x] [Review][Patch] The value update and the audit-correction write are two independent `SaveChangesAsync` calls with no shared transaction — if recording the correction fails after the value update commits, the value change persists silently with no correction note, defeating AC #3's core promise [src/EnergyTracker.Application/EditMeterReading.cs:25-38] — fixed: new `IUnitOfWork` port (`src/EnergyTracker.Application/Ports/IUnitOfWork.cs`) + `UnitOfWork` adapter (`src/EnergyTracker.Infrastructure/Adapters/UnitOfWork.cs`) wraps both writes in one DB transaction; `UpdateKwhValueAsync`/`RecordAsync` are unchanged, only their orchestration in `EditMeterReading` is now transactional.
- [x] [Review][Patch] `page` has no upper bound in `GetMeterReadingHistory.ExecuteAsync`'s validation, so a large value can integer-overflow `GetPageForMainMeterAsync`'s `Skip((page - 1) * pageSize)` into an unhandled exception (500) instead of a graceful empty page [src/EnergyTracker.Infrastructure/Adapters/MeterReadingRepository.cs:108] — fixed: added a long-arithmetic overflow guard in `GetMeterReadingHistory.ExecuteAsync` that throws `MeterReadingValidationException` (→ 400) instead.
- [x] [Review][Patch] `GetLatestForEntitiesAsync` has no tiebreak for two corrections sharing an identical `CorrectedAtUtc`, unlike the codebase's own `FindImmediatelyPrecedingAsync` precedent (`ThenByDescending(r => r.Id)`) — which correction surfaces as "latest" is nondeterministic on a tie [src/EnergyTracker.Infrastructure/Adapters/AuditCorrectionRecorder.cs:40] — fixed: added `.ThenByDescending(a => a.Id)`.
- [x] [Review][Patch] `ToHistoryPageResponse` constructs `MeterReadingHistoryPageResponse` positionally with 3 adjacent same-typed `int` fields (`TotalCount`, `Page`, `PageSize`) — this story's own Dev Notes/Completion Notes claim named-argument construction was applied "at every multi-field response-mapping call site" to pre-empt Story 2.7's transposition finding, but this call site was missed [src/EnergyTracker.Api/Endpoints/MeterReadingEndpoints.cs:119-124] — fixed: switched to named arguments.
- [x] [Review][Patch] Every row's "Edit" button shares the identical accessible name ("Edit") with nothing distinguishing rows — indistinguishable to screen-reader/voice-control users and to `getByRole('button', { name: 'Edit' })`-style queries [web/src/components/meter-reading/meter-reading-history-page.tsx:108-110] — fixed: added a per-row `aria-label` (new `meterReadingHistory.editTriggerFor` i18n key, en/de) using the row's formatted timestamp; regression test added.
- [x] [Review][Patch] The `loading` state is tracked but never rendered — all three body branches (error/empty/table) require `!loading`, so the content area goes fully blank with zero feedback during the initial fetch and every page navigation [web/src/components/meter-reading/meter-reading-history-page.tsx:24,73-116] — fixed: added a loading branch (new `meterReadingHistory.loading` i18n key, en/de).
- [x] [Review][Patch] Previous/Next pagination buttons aren't disabled while a fetch is in flight (only at the first/last page), allowing redundant overlapping requests during rapid clicking [web/src/components/meter-reading/meter-reading-history-page.tsx:121,127] — fixed: both buttons now also disable on `loading`.

## Dev Notes

### Architecture compliance (binding, not optional)

- **AD-4 — first `MeterReading.Version` column, second use of the pattern.** `MeterReading.cs`'s own comment names this story explicitly as the trigger for adding it. Copy `Household.Version`'s shape and `HouseholdRepository.UpdateYearlyBaselineAsync`'s optimistic-concurrency mechanics exactly (Task 1, Task 4) — this is the second, not the first, implementation of AD-4's portable-version-column rule; do not invent a variant.
- **AD-11 — first `AuditCorrection` table, designed for a second consumer later.** This story is the first to build the shared mechanism AD-11 specifies (`EntityType`/`EntityId`/`FieldName`/`OldValue`/`NewValue`/`CorrectedAtUtc`, one `IAuditCorrectionRecorder`). A future Epic 5 Tariff-editing story is expected to reuse the same table/port unmodified — keep `EntityType`/`FieldName` as plain strings (not a `MeterReading`-specific enum) and keep the port's method signatures entity-agnostic, exactly as designed in Task 2.
- **AD-12 — no changes to the regression-prompt invariant.** "At most one open `MeterRegressionPrompt` per Main Meter" is read-only context for this story (Task 5's pending flag, Task 6's explicit no-interaction decision) — this story does not create, resolve, or otherwise mutate `MeterRegressionPrompt` rows.
- **AD-14 — no new exposure risk.** Every new field this story returns (`Version`, `IsPendingRegression`, `CorrectedFromKwhValue`) is derived from `MeterReading`/`MeterRegressionPrompt`/`AuditCorrection` data, never `SmartPlugReading`/`Event` — `PatternDetectiveDoesNotReferenceSmartPlugOrEventDataTests` needs no changes.
- **AD-16 (frontend stack note) — edits are explicitly not offline-queued.** "Offline queue... Meter Reading creation only (not edits) queues locally via IndexedDB... don't extend this offline pattern to other writes without a matching architecture decision." Task 11's `EditMeterReadingDialog` must **not** route through `meter-reading-sync.ts`'s `attemptSend`/offline-queue machinery — it's a plain online-only `fetch` (mirrors `SetYearlyBaseline`'s frontend call shape, not `LogReadingSheet`'s).
- **AD-18 — storage stays locale-neutral, display doesn't.** Task 6's `AuditCorrection.OldValue`/`NewValue` are `InvariantCulture` strings (storage); Task 10's rendered kWh/date figures use `Intl.NumberFormat(locale, ...)`/`Intl.DateTimeFormat(locale, ...)` (display) — never mix the two directions.
- **Consistency Conventions — route reuse, not a new resource name.** AC #5 is explicit: the paginated list is a new verb on the *existing* `/api/meter-readings` route, not a new `/api/meter-reading-history` resource. `PUT /api/meter-readings/{id}` follows the same reused-route discipline for the edit path.
- **"Confirmed with Ralf" section above (surface shape, entry point, no nav-chrome slot)** is binding for Tasks 10 and 12 — do not re-derive a different placement from the mockups (there isn't one) or from `EXPERIENCE.md`'s pre-freeze Information Architecture table, which still says "Editing a past Meter Reading... Trend History" (line 93) — that line predates FR-31 and is **superseded** by this story and the epic's own Story 2.8 section, which explicitly reassigns per-Reading browsing/editing to this new surface and keeps Trend History aggregate-only (AC #2). Don't "fix" this story to match that stale IA row.

### Existing code this story builds on (read before writing anything)

- `src/EnergyTracker.Domain/MeterReading.cs` — the entity Task 1 extends; read its Version-deferral comment (being removed) and every other field before touching it.
- `src/EnergyTracker.Application/CreateMeterReading.cs` — the validation logic Task 3 extracts from, and the `IMeterReadingRepository`/`IMeterRegressionPromptRepository`/`IStatusRecomputeService` composition pattern (constructor-injected ports, one `ExecuteAsync`) every new use case in this story follows.
- `src/EnergyTracker.Infrastructure/Adapters/HouseholdRepository.cs:97-122` (`UpdateYearlyBaselineAsync`) — the exact optimistic-concurrency mechanics Task 4's `UpdateKwhValueAsync` copies.
- `src/EnergyTracker.Api/Endpoints/HouseholdEndpoints.cs:94-122` — the exact 400/409 catch-and-map shape Task 7's PUT route copies, including the "message only, not full server state" 409 precedent.
- `src/EnergyTracker.Application/Ports/IMeterRegressionPromptRepository.cs` / `GetOpenMeterRegressionPrompt.cs` — `GetOpenForHouseholdAsync`'s exact return shape Task 5 consumes for the pending flag; AD-12's "at most one" guarantee is what makes this a flat equality check instead of a per-row query.
- `src/EnergyTracker.Infrastructure/EnergyTrackerDbContext.cs` — every `DbSet`/`HasQueryFilter` pair Task 1 adds a new line to; read the existing 10 lines to match formatting/order exactly.
- `web/src/components/settings/settings-page.tsx` — the page shell Task 10's `MeterReadingHistoryPage` mirrors (heading + Back button row, no `NavChrome`... actually `SettingsPage` *does* render `NavChrome` since Settings has a tab — Task 10 deliberately omits it; read `nav-chrome.tsx`'s 4-entry `NavTab` type to confirm there's genuinely no slot before assuming one should be added).
- `web/src/components/meter-reading/log-reading-sheet.tsx` — the `UnitInput` kWh-field setup Task 11 mirrors, and the `ApiError`/error-message fallback pattern.
- `web/src/components/dashboard/dashboard-page.tsx:91-105` (`detailTrigger`) — the exact trigger placement/classnames Task 12's history trigger mirrors, and the `showPopulated` guard it reuses.
- `web/src/App.tsx:38,242-243` (`view` state, `'settings'` branch) — the exact pattern Task 12 extends with a third value; read the comment at `App.tsx:33-37` explaining why this is local state, not a route, before adding one.
- `web/src/lib/status-api.ts` — the `ApiError`/`toApiError`/fetch pattern Task 9 replicates verbatim.
- `web/src/lib/glass-classnames.ts` — `GLASS_MODAL_CLASSNAME`, reused as-is by Task 11.

### Previous story intelligence (Stories 2.3, 2.4, 2.7)

- Story 2.3 built the entire `MeterRegressionPrompt`/AD-12 machinery this story reads from (Task 5) but never writes to — `GetOpenMeterRegressionPromptTests.cs` is the closest existing test-mocking precedent for `GetMeterReadingHistoryTests.cs` (Task 14).
- Story 2.4's `CreateMeterReading.cs` established the `MaxKwhValue`/timestamp-bounds validation this story's Task 3 extracts rather than duplicates — the exact same "don't let two use cases silently drift on the same business rule" reasoning Story 2.7's Task 4 already applied once (there, to the pace/baseline difference sign logic).
- Story 2.7 is the direct structural precedent for this entire story: no mockup, no `UX-DR*` citation, additive-only changes to an existing endpoints file, the `Dialog`+`GLASS_MODAL_CLASSNAME` shell reused rather than a bespoke visual language, and — critically — the exact same "confirmed with Ralf during story creation" discipline this story's own header section follows for its surface-shape/entry-point decision. Story 2.7's review also fixed a `DbUpdateConcurrencyException`-adjacent-class of bug (8-arg positional-construction risk) by switching to named arguments at every multi-field record construction site — apply that same discipline to `MeterReadingHistoryItemResponse`'s 7 positional fields (several adjacent same-typed) up front, don't wait for a review finding to catch it.
- `deferred-work.md`'s existing entry on `ResolveMeterRegressionPrompt` not triggering a Status recompute ("in-spec per AD-7's two-call-site rule") is the direct precedent Task 6 follows for **not** adding a recompute on edit — read it before assuming a recompute call is missing.

### File structure / conventions to follow exactly

- Backend: one use-case class per file (`GetMeterReadingHistory.cs`, `EditMeterReading.cs` — 2 new files, not folded into `CreateMeterReading.cs`). Exceptions each get their own file, named `{Concept}Exception.cs`, matching every existing exception in `EnergyTracker.Application`.
- New domain entity `AuditCorrection.cs` lives flat in `EnergyTracker.Domain`, same as `MeterReading.cs`/`MeterRegressionPrompt.cs` — no sub-namespace.
- Frontend: new components live in the existing `web/src/components/meter-reading/` folder (not a new `meter-reading-history/` folder) — this is the same feature domain as the existing Log Reading / Regression Prompt components, per the established `web/src/components/{feature}` grouping convention.
- `web/src/lib/meter-reading-history-api.ts` lives flat under `web/src/lib/`, alongside `status-api.ts`/`meter-regression-api.ts` — same convention.
- `verbatimModuleSyntax: true` — `import type { MeterReadingHistoryItemDto, MeterReadingHistoryPageDto }` wherever type-only.
- Both `en-US` and `de-DE` `translation.json` updated together (Task 13), verified for key-set parity — never one locale alone.
- Test naming: .NET — `Snake_case_with_underscores`, Shouldly, `TestContext.Current.CancellationToken`, new test files for the 2 new use cases (no existing file to extend), existing `MeterReadingEndpointsTests.cs` extended in place (no new Api.Tests file). Frontend — colocated, Vitest + Testing Library, `vi.stubGlobal('fetch', ...)` mocking per this codebase's established pattern.

### Testing standards summary

.NET: xunit.v3.mtp-v2, Shouldly, NSubstitute for Application-layer mocks, real Postgres+SqlServer via Testcontainers for `EnergyTracker.Api.Tests` (`EnergyTrackerApiFactory`, reuse `MeterReadingEndpointsTests.cs`'s existing `CreateHouseholdAsync`/`CountMeterReadingRowsAsync` helpers rather than rewriting them). Frontend: Vitest + `@testing-library/react`, `jsdom`, globals on, i18next real-catalog (no test-provider wrapper needed, per Story 2.5's established precedent).

### Project Structure Notes

- Alignment: follows the exact `web/src/components/{feature}` + colocated-test convention, the flat `web/src/lib/` convention, and the one-use-case-class-per-file backend convention every prior Epic 2 story already established. No deviation.
- This is the largest-surface-area story in Epic 2 so far — 2 new domain entities' worth of concept (Version column + AuditCorrection table), 2 new use cases, 2 new endpoints, a new frontend page, and a first-of-its-kind pagination convention — unlike Story 2.7's deliberately minimal "reuse everything" scope. The size comes from this story being the first to wire up two previously-deferred architecture invariants (AD-4's second use, AD-11's first use) at once, not from scope creep — resist the temptation to shrink Task 1/2's scope to "just enough for this story," since a future Tariff-editing story is expected to reuse `AuditCorrection`/`IAuditCorrectionRecorder` unmodified.

### References

- [Source: `_bmad-artifacts/planning/epics/epic-2-meter-reading-pattern-detective-status-core.md#Story 2.8`] — story statement + AC source (verbatim).
- [Source: `_bmad-artifacts/planning/prds/prd-energy-tracker-2026-08-08/prd/4-features.md#FR-31`] — FR consequences, exact wording this story's ACs mirror.
- [Source: `_bmad-artifacts/planning/epics/requirements-inventory.md#FR-31, #NFR8`] — FR-31's testability note, NFR8's audit-trail wording.
- [Source: `_bmad-artifacts/planning/architecture/architecture-energy-tracker-2026-08-09/ARCHITECTURE-SPINE/invariants-rules.md#AD-4, #AD-11, #AD-12, #AD-14, #AD-16, #AD-18`] — exact AD rule text this story must not violate; AD-11 and AD-4 are the two invariants this story activates for the first/second time respectively.
- [Source: `_bmad-artifacts/planning/architecture/architecture-energy-tracker-2026-08-09/ARCHITECTURE-SPINE/consistency-conventions.md`] — route-reuse convention (AC #5's direct basis) and the 409-with-current-server-state convention (whose actual codebase precedent is message-only — see Dev Notes).
- [Source: `_bmad-artifacts/planning/ux-designs/ux-energy-tracker-2026-08-08/EXPERIENCE.md`] — nav-chrome's fixed 4-tab shape (line 22), the stale pre-freeze IA row this story supersedes (line 93), and the "contextual entry point, not a new tab" precedent this story's confirmed entry-point decision follows.
- [Source: `_bmad-artifacts/planning/ux-designs/ux-energy-tracker-2026-08-08/DESIGN/components.md`] — "standard shadcn components (Dialog, ..., Table) are used unmodified" — basis for Task 8.
- [Source: `_bmad-artifacts/implementation/2-7-status-calculation-detail.md`] — direct structural precedent for this story's entire "no mockup, confirmed with Ralf, additive-only" shape; the 8-arg positional-construction review finding this story pre-empts with named arguments.
- [Source: `_bmad-artifacts/implementation/2-3-meter-reading-regression-detection-classification.md`, `_bmad-artifacts/implementation/2-4-gap-tolerant-rolling-baseline-status-computation.md`] — `MeterRegressionPrompt`/AD-12 and `CreateMeterReading`'s validation-bounds history this story reads from/extracts from.
- [Source: `_bmad-artifacts/implementation/deferred-work.md`] — the existing "resolve doesn't recompute Status" entry, the direct precedent for Task 6's identical scope decision.
- [Source: `src/EnergyTracker.Domain/MeterReading.cs`, `src/EnergyTracker.Domain/Household.cs`, `src/EnergyTracker.Application/CreateMeterReading.cs`, `src/EnergyTracker.Application/Ports/IMeterReadingRepository.cs`, `src/EnergyTracker.Infrastructure/Adapters/MeterReadingRepository.cs`, `src/EnergyTracker.Infrastructure/Adapters/HouseholdRepository.cs`, `src/EnergyTracker.Api/Endpoints/MeterReadingEndpoints.cs`, `src/EnergyTracker.Api/Endpoints/HouseholdEndpoints.cs`, `src/EnergyTracker.Infrastructure/EnergyTrackerDbContext.cs`] — existing backend code this story extends read-first.
- [Source: `web/src/components/settings/settings-page.tsx`, `web/src/components/dashboard/dashboard-page.tsx`, `web/src/components/dashboard/nav-chrome.tsx`, `web/src/components/meter-reading/log-reading-sheet.tsx`, `web/src/App.tsx`, `web/src/lib/status-api.ts`, `web/src/lib/glass-classnames.ts`] — existing frontend code this story extends read-first.
- [Source: `web/src/locales/en-US/translation.json`, `de-DE/translation.json`] — existing key sets this story extends (`dashboard.*`, `meterReading.*` for copy reuse) and the new `meterReadingHistory.*` namespace.

## Dev Agent Record

### Agent Model Used

Claude (claude-sonnet-5), via bmad-dev-story workflow.

### Debug Log References

- `dotnet build EnergyTracker.sln` — one compile error (missing `using EnergyTracker.Application;` in `MeterReadingRepository.cs` after adding `MeterReadingConcurrencyConflictException`), fixed immediately, then 0 errors.
- `./scripts/add-migration.sh AddMeterReadingVersionAndAuditCorrection` — succeeded on both Postgres and SqlServer providers in one commit (AD-2).
- `dotnet test EnergyTracker.sln` — 346/346 passed (Application 184, Architecture, Api.Tests 125 via Testcontainers, Infrastructure.Tests).
- `npx vitest run` (web) — 184/184 passed across 24 files; `npx tsc -b` and `npx oxlint` both clean (only 3 pre-existing, unrelated `only-export-components` warnings).

### Completion Notes List

- Implemented all 16 tasks: `MeterReading.Version` (AD-4, second use of the pattern) + `AuditCorrection` table (AD-11, first use) with a single migration covering both providers; `IAuditCorrectionRecorder` port/adapter; shared `MeterReadingValidation.ValidateKwhValue` extracted from `CreateMeterReading` and reused by `EditMeterReading`; repository extensions `GetPageForMainMeterAsync`/`UpdateKwhValueAsync`; `GetMeterReadingHistory`/`EditMeterReading` use cases; paginated `GET /api/meter-readings` + `PUT /api/meter-readings/{id}` endpoints; frontend `MeterReadingHistoryPage` + `EditMeterReadingDialog` + API client; Dashboard history trigger + `App.tsx` `'history'` view; en-US/de-DE i18n (byte-for-byte key parity verified via script); backend + frontend test coverage; `deferred-work.md` entry for the no-Status-recompute-on-edit scope decision (Task 6/16).
- Followed the story's own "Confirmed with Ralf" surface-shape/entry-point/no-nav-chrome-slot decisions exactly — no deviation, no new mockup invented.
- `MeterReadingResponse`/`MeterReadingHistoryItemResponse` use named-argument construction at the multi-field response-mapping call site (`ToHistoryItemResponse`), pre-empting the positional-construction risk Story 2.7's review flagged, per this story's own Dev Notes instruction.
- `MeterReading.KwhValue` changed from `init` to a mutable `set` — required for `UpdateKwhValueAsync`'s in-place mutation (mirrors `Household.YearlyBaselineKwh`'s existing mutable-setter shape); no other field's mutability changed.
- The Dashboard's second (History) trigger is composed into the same `detailTrigger` prop `StatusCard` already renders (a `<>...</>` fragment bundling both buttons) rather than adding a new prop to `status-card.tsx` — keeps the diff scoped to `dashboard-page.tsx` exactly as the story's Task 12 specifies, satisfies the same `showPopulated` guard and "directly below" placement without touching `StatusCard`'s prop surface.
- Per Task 16's confirmation subtask: no `docs/*.md` changes were needed beyond the `deferred-work.md` entry — no new operator-facing config, adapter, or env var was introduced.

### File List

**Backend — new**
- `src/EnergyTracker.Domain/AuditCorrection.cs`
- `src/EnergyTracker.Application/Ports/IAuditCorrectionRecorder.cs`
- `src/EnergyTracker.Infrastructure/Adapters/AuditCorrectionRecorder.cs`
- `src/EnergyTracker.Infrastructure/Configurations/AuditCorrectionConfiguration.cs`
- `src/EnergyTracker.Application/MeterReadingValidation.cs`
- `src/EnergyTracker.Application/MeterReadingNotFoundException.cs`
- `src/EnergyTracker.Application/MeterReadingConcurrencyConflictException.cs`
- `src/EnergyTracker.Application/GetMeterReadingHistory.cs`
- `src/EnergyTracker.Application/EditMeterReading.cs`
- `src/EnergyTracker.Application/Ports/IUnitOfWork.cs` (added during code review — transactional EditMeterReading fix)
- `src/EnergyTracker.Infrastructure/Adapters/UnitOfWork.cs` (added during code review — transactional EditMeterReading fix)
- `src/EnergyTracker.Infrastructure.Migrations.Postgres/Migrations/20260823164619_AddMeterReadingVersionAndAuditCorrection.cs`
- `src/EnergyTracker.Infrastructure.Migrations.Postgres/Migrations/20260823164619_AddMeterReadingVersionAndAuditCorrection.Designer.cs`
- `src/EnergyTracker.Infrastructure.Migrations.SqlServer/Migrations/20260823164622_AddMeterReadingVersionAndAuditCorrection.cs`
- `src/EnergyTracker.Infrastructure.Migrations.SqlServer/Migrations/20260823164622_AddMeterReadingVersionAndAuditCorrection.Designer.cs`
- `tests/EnergyTracker.Application.Tests/GetMeterReadingHistoryTests.cs`
- `tests/EnergyTracker.Application.Tests/EditMeterReadingTests.cs`

**Backend — modified**
- `src/EnergyTracker.Domain/MeterReading.cs`
- `src/EnergyTracker.Infrastructure/Configurations/MeterReadingConfiguration.cs`
- `src/EnergyTracker.Infrastructure/EnergyTrackerDbContext.cs`
- `src/EnergyTracker.Application/CreateMeterReading.cs`
- `src/EnergyTracker.Application/Ports/IMeterReadingRepository.cs`
- `src/EnergyTracker.Infrastructure/Adapters/MeterReadingRepository.cs`
- `src/EnergyTracker.Api/Endpoints/MeterReadingEndpoints.cs`
- `src/EnergyTracker.Api/Program.cs`
- `src/EnergyTracker.Infrastructure.Migrations.Postgres/Migrations/EnergyTrackerDbContextModelSnapshot.cs`
- `src/EnergyTracker.Infrastructure.Migrations.SqlServer/Migrations/EnergyTrackerDbContextModelSnapshot.cs`
- `tests/EnergyTracker.Api.Tests/MeterReadingEndpointsTests.cs`

**Frontend — new**
- `web/src/components/ui/table.tsx` (shadcn-generated)
- `web/src/lib/meter-reading-history-api.ts`
- `web/src/lib/meter-reading-history-api.test.ts`
- `web/src/components/meter-reading/meter-reading-history-page.tsx`
- `web/src/components/meter-reading/meter-reading-history-page.test.tsx`
- `web/src/components/meter-reading/edit-meter-reading-dialog.tsx`
- `web/src/components/meter-reading/edit-meter-reading-dialog.test.tsx`

**Frontend — modified**
- `web/src/App.tsx`
- `web/src/App.test.tsx`
- `web/src/components/dashboard/dashboard-page.tsx`
- `web/src/components/dashboard/dashboard-page.test.tsx`
- `web/src/locales/en-US/translation.json`
- `web/src/locales/de-DE/translation.json`

**Documentation**
- `_bmad-artifacts/implementation/deferred-work.md`
