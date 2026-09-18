---
baseline_commit: ce0cf5a8714ef5d589fbb05cbd3d5722cd684957
---

# Story 6.2: Event History View

Status: ready-for-dev

<!-- Note: Validation is optional. Run validate-create-story for quality check before dev-story. -->

## Story

As a Household member,
I want to see the Events I've logged, with their tags,
so that the context I captured is actually readable back — and so a tag whose Room/Power Point/Device was later deleted still reads as plain text rather than a broken reference.

## Acceptance Criteria

1. **Given** Events logged for my Household, **when** I open the Event history surface, **then** they are listed in reverse-chronological order by `OccurredAt` (the date the Event occurred, not the date it was entered), scoped to my own Household only (AD-3).
2. **Given** an Event with a tag, **when** it is displayed, **then** the tag renders from the stored `TaggedEntityName` snapshot verbatim, never re-derived (AD-10 — see Dev Notes).
3. **Given** an Event whose tagged Room/Power Point/Device was archived after the Event was logged, **when** it is displayed, **then** the tag still renders as inert plain text, with no broken reference, no error, and no visual "deleted" treatment — **this closes Story 6.1's AC #4 display half** (FR-16).
4. **Given** an Event with no tag, **when** it is displayed, **then** it renders cleanly with no empty tag affordance or placeholder.
5. **Given** a Household with no Events yet, **when** the surface is opened, **then** an empty state is shown in the product's established quiet voice — never an error.
6. **Given** the Event list, **when** rendered, **then** no consumption figure is derived from Event data and no Event-derived figure appears alongside the Main Meter total (AD-14 — a row count is permitted; see Dev Notes).

## Tasks / Subtasks

- [ ] Task 1: Read path — Application + Infrastructure (AC: #1, #2)
  - [ ] 1.1 Extend `src/EnergyTracker.Application/Ports/IEventRepository.cs`:
    ```csharp
    Task<(IReadOnlyList<Event> Items, int TotalCount)> GetPageForHouseholdAsync(
        int page, int pageSize, CancellationToken cancellationToken);
    ```
    **No `householdId` parameter** — AD-3's DbContext query filter already scopes the read, exactly as `ITaggingScaffoldRepository.List*Async` does (that port's doc comment says so outright).
  - [ ] 1.2 Implement in `src/EnergyTracker.Infrastructure/Adapters/EventRepository.cs`:
    - Order by `OccurredAt` DESC, then `CreatedAtUtc` DESC as a stable tiebreaker (two Events backfilled to the same `OccurredAt` must not reorder between pages).
    - Get `TotalCount` via `CountAsync` on the same filtered query **before** `Skip`/`Take` — never by materializing the set and counting in memory.
    - Use `AsNoTracking()` — this is a read path.
    - Never `Find()`, `FromSqlRaw`, or `IgnoreQueryFilters()` (project-context names these as the specific AD-3 bypasses).
  - [ ] 1.3 Add `src/EnergyTracker.Application/GetEventHistory.cs`. Exact shape — do not re-derive it from `GetMeterReadingHistory`:
    ```csharp
    public record EventHistoryPage(IReadOnlyList<Event> Items, int TotalCount, int Page, int PageSize);

    public class GetEventHistory(IEventRepository repository)
    {
        private const int MaxPageSize = 100;

        public async Task<EventHistoryPage> ExecuteAsync(int page, int pageSize, CancellationToken cancellationToken)
    ```
    **The signature takes no `householdId`** — unlike `GetMeterReadingHistory`, which needs one only to look up a Main Meter. Events have no such parent.
  - [ ] 1.4 Copy `GetMeterReadingHistory`'s three guards verbatim, including the overflow guard — it exists because `Skip((page - 1) * pageSize)` overflows int32 for an absurd page, and is checked in long arithmetic so the check itself cannot overflow:
    ```csharp
    if (page < 1) throw new EventValidationException($"page must be at least 1, got '{page}'.", "event.page_invalid");
    if (pageSize < 1 || pageSize > MaxPageSize) throw new EventValidationException($"pageSize must be between 1 and {MaxPageSize}, got '{pageSize}'.", "event.page_size_invalid");
    if ((long)(page - 1) * pageSize > int.MaxValue) throw new EventValidationException($"page {page} is out of range for pageSize {pageSize}.", "event.page_invalid");
    ```
    Reuse the existing two-argument `EventValidationException(message, errorCode)` from 6.1 — do not add a new exception type and do not add a message-only overload.
- [ ] Task 2: Index migration (AC: #1)
  - [ ] 2.1 In `src/EnergyTracker.Infrastructure/Configurations/EventConfiguration.cs`, **replace** `HasIndex(e => e.HouseholdId)` with `HasIndex(e => new { e.HouseholdId, e.OccurredAt, e.CreatedAtUtc })`. The single-column index becomes strictly redundant once the composite leads with the same column, and the third column covers the tiebreaker so the sort is fully index-ordered on both providers. (`MeterReading` keeps both only because its composite leads with `MainMeterId`, a *different* column — not a precedent for keeping both here.)
  - [ ] 2.2 Add the migration to **both** provider projects via `scripts/add-migration.sh AddEventOccurredAtIndex` (AD-2 — never `dotnet ef migrations add` directly). Confirm the generated migration includes the DROP of the old single-column index on both providers.
- [ ] Task 3: API endpoint (AC: #1)
  - [ ] 3.1 Add `api.MapGet("/events", ...)` to the **existing** `src/EnergyTracker.Api/Endpoints/EventEndpoints.cs` — do not create a second endpoints file. Mirror `MeterReadingEndpoints.MapGet("/meter-readings")`'s **parameter shape and 403 gate** (`int page = 1, int pageSize = 20`, file-local `TryGetHouseholdId`). The error path deliberately diverges — see 3.2.
  - [ ] 3.2 Call `TryGetHouseholdId(householdAccessor, out _, out var forbidden)` purely as the **auth gate** (403 when the principal has no Household); the id itself is discarded, because AD-3's filter does the scoping. Catch `EventValidationException` and return it through the existing private `Problem(detail, errorCode, statusCode)` helper so the response carries the `errorCode` extension the SPA localizes. **Do not call `Results.Problem` directly** — `MeterReadingEndpoints` does, and that predates 6.1's error-code contract; copying it would regress a fix that shipped days ago.
  - [ ] 3.3 Reuse the existing `EventResponse`; add `EventHistoryPageResponse(IReadOnlyList<EventResponse> Items, int TotalCount, int Page, int PageSize)` and a private `ToHistoryPageResponse` mapper (naming follows `MeterReadingHistoryPageResponse`/`ToHistoryPageResponse`). Register `GetEventHistory` in `Program.cs` beside the existing `AddScoped<CreateEvent>()`.
- [ ] Task 4: Frontend Event history card (AC: #1–#6)
  - [ ] 4.1 Extend `web/src/lib/event-api.ts` with `fetchEventHistory(page, pageSize): Promise<EventHistoryPageDto>` (DTO named for symmetry with `MeterReadingHistoryPageDto`). Keep the file's existing local `ApiError`/`toApiError` — per this repo's convention these are copied per API file, not shared (see the comment at the top of `tariff-api.ts`).
  - [ ] 4.2 **Move** the `errorCode` → catalog mapping out of `log-event-sheet.tsx` (its `messageFor` helper, lines ~95-107: strip the `^event\.` prefix, look up `event.errors.<code>`, fall back to `event.errorGeneric`, special-case 401 → `event.errors.sessionExpired`) into a shared exported helper in `event-api.ts`, and update the sheet to use it. Both surfaces need identical behavior; duplicating it invites one of them to drift back to rendering `err.detail`, the English text AD-18 forbids.
  - [ ] 4.3 Add `web/src/components/event/events-card.tsx`, modelled on `web/src/components/meter-reading/meter-readings-card.tsx`. Match that card's idiom precisely, because all three Trend History cards share it:
    - `GlassCard` wrapping `<details className="group">`, **collapsed by default** (NFR15's "says less, on purpose").
    - `<summary>` containing `<span role="heading" aria-level={2}>` with the summary label, so screen readers can navigate to the card without disturbing the summary's toggle semantics.
    - `const PAGE_SIZE = 20` as a module constant; a `locale: string` prop formatted via `new Intl.DateTimeFormat(locale, { dateStyle: 'medium', timeStyle: 'short' })` for `OccurredAt` (AD-18 — locale drives all date formatting).
    - Same loading / empty / error handling and pagination affordance.
  - [ ] 4.4 Render the tag as plain text from `taggedEntityName`. **Never** fetch `/api/rooms`, `/api/power-points`, or `/api/devices` to resolve or verify it, and never branch on whether the tagged entity still exists (AD-10 — see Dev Notes).
  - [ ] 4.5 Mount it in `web/src/components/trend-history/trend-history-page.tsx` between `<MeterReadingsCard/>` and `<PerPlugDataCard/>` (currently lines 85-87). **Update that file's header comment**, which documents the three-card order and its rationale ("Card order: chart, then Meter Readings … the tree stays last") — a fourth card silently invalidates it.
  - [ ] 4.6 i18n (AD-18 — both catalogs, same commit, identical key sets): card copy goes under **`trendHistory.eventsCard.*`** (`summary`/`expand`/`collapse`/empty/error), matching its two sibling cards — *not* under `event.*`. The new pagination error codes **must** go under `event.errors.page_invalid` / `event.errors.page_size_invalid`, because the shared mapper from 4.2 resolves that prefix.
- [ ] Task 5: Tests
  - [ ] 5.1 `GetEventHistoryTests.cs` (Application.Tests): reverse-chronological ordering by `OccurredAt`; the `CreatedAtUtc` tiebreaker for equal `OccurredAt`; each pagination guard throws `EventValidationException` with the expected `ErrorCode`; an empty Household returns an empty page rather than throwing.
  - [ ] 5.2 `EventRepositoryTests.cs` (Infrastructure.Tests): add tests **to the abstract `EventRepositoryTestsBase`**, not to a provider subclass — the `PostgresEventRepositoryTests`/`SqlServerEventRepositoryTests` subclasses already exist and inherit automatically, so adding to one subclass silently halves AD-2 coverage. Cover ordering, paging boundaries, and that the AD-3 filter excludes another Household's Events from the page.
  - [ ] 5.3 `EventEndpointsTests.cs` (Api.Tests): 200 with the page shape; 400 carrying an `errorCode` for a bad `page`/`pageSize`; 403 when the caller has no Household; and an Event whose tag was archived after logging still returning its original `taggedEntityName` (AC #3, end-to-end).
  - [ ] 5.4 `events-card.test.tsx` (Vitest): renders a tagged and an untagged Event; empty state; error state; and — the AD-10 guard — renders the tag of an Event whose entity was archived while **asserting no request was made to `/api/rooms`, `/api/power-points`, or `/api/devices`**. Assert on the fetch stub, not just the DOM: a DOM-only assertion still passes if someone adds a resolve-then-render path that happens to return the same name.
  - [ ] 5.5 Update `web/src/components/trend-history/trend-history-page.test.tsx` — **this file will break otherwise.** Its `mockRoutes` helper allowlists `/api/status/history`, `/api/meter-readings`, `/api/smart-plug-readings` and falls through to `jsonResponse(null)`, so an unhandled `/api/events` returns 200 with a null body, `response.json()` throws, and the new card renders its error state inside *every* existing test. Add an `/api/events` branch to both fetch stubs in that file, and extend the ordering test (`'renders the chart, the Meter Readings card, and the Per-Plug card in that order'`, line 34 — it asserts document position) to include the Events card.
- [ ] Task 6: Close out Story 6.1 (AC: #3)
  - [ ] 6.1 In `_bmad-artifacts/implementation/6-1-event-logging.md`, tick the AC #4 decision item under `### Review Findings` — the one reading "This AC closes only when the Event read path ships in the follow-up story" — and note that 6.2 closed it.
  - [ ] 6.2 Set that story's `Status:` to `done` and add a Change Log line, **provided** its live-verification gate remains satisfied and no other review item is open.
  - [ ] 6.3 Set `6-1-event-logging: done` in `sprint-status.yaml` with a dated note. Story 6.1 is currently `in-progress` blocked on exactly this AC — without this task it stays blocked indefinitely.
- [ ] Task 7: Live verification (AC: #3, #5)
  - [ ] 7.1 This project's standing gate requires a live Auth0/Chrome check before review→done; it does not defer. AC #3 is a *display* behavior, which unit tests can only simulate — and 6.1's own review found a real layout defect only once a live render was run.
  - [ ] 7.2 Run the stack per `docs/local-development.md` (Postgres via compose → `./scripts/migrate.sh` → `./scripts/run-api.sh` → `npm --prefix web run dev`; Vite serves **HTTPS** on 5173, and Chrome is the supported browser for plain-localhost cookie behavior). Then, signed in: log an Event with a tag → archive that Room/Power Point/Device → reload Trend History → confirm the tag still reads as plain text with no error and no "deleted" styling. Confirm the empty state on a Household with no Events. Record the outcome in the Dev Agent Record.

## Dev Notes

### Where the surface lives (resolved ambiguity)

**Put the Event list on Trend History, as a card between the Meter Readings card and the Per-Plug card** — not inside the Log Event sheet. `EXPERIENCE.md`'s IA row could be read either way, but the sheet is a transient bottom `Sheet` sized for fast entry (6.1's AC #1: "comparable effort to a Meter Reading entry — not a form"), while Trend History already hosts exactly this shape in `meter-readings-card.tsx`. Story 6.3 renders its correlation "inline with the Event", and a persistent list row is a better host for that than a sheet the household must reopen.

**If this needs revisiting:** flag it in code review rather than silently reworking — the card is self-contained and still cheap to move.

### AD-10: render the snapshot, never re-derive it (AC #2, #3)

This is the constraint the story exists for. `Event.TaggedEntityName` was captured by value at write time in 6.1. The display path renders that string and nothing else: no join through `TaggedEntityId`, no lookup against the scaffold endpoints, no "is this still live?" check, no strikethrough or "(deleted)" decoration.

Re-deriving it "would reproduce the exact 'live join rewrites history' failure AD-10 exists to prevent" — `invariants-rules.md:161`, in the bullet titled "AD-10 constraint (named, not implicit)" (note: that bullet sits inside the **AD-22** section, not under the `## AD-10` heading — grepping AD-10's own section won't find it).

6.1's review verified the *persistence* half live: the snapshot still read `HiFi` after its Power Point was archived. Task 5.4 is the guard for the *display* half, and it asserts on the absence of a network call for the reason given there.

### AD-3 and AD-14

- **AD-3:** neither the port method nor the use case takes a `householdId`; the global query filter on `dbContext.Events` (added in 6.1) does the scoping. The endpoint's `TryGetHouseholdId` call is an auth gate only. Task 5.2's cross-household test is the guard.
- **AD-14:** a **row count is permitted** — `TotalCount`, and a summary label like the sibling card's `"Meter Readings — {{count}} logged"`, are not consumption data. What AD-14 forbids is any kWh/consumption figure derived from Event data, and any Event-derived figure rendered alongside the Main Meter total. Separately, `tests/EnergyTracker.Architecture.Tests/PatternDetectiveDoesNotReferenceSmartPlugOrEventDataTests.cs` source-scans 9 specific files for the word `Event` and fails the build if found (6.1's Dev Notes lists them) — **do not touch those 9 files**; this story has no reason to.

### No write side effects

AD-7 names exactly two Status-recompute call sites (Meter-Reading-create, Smart-Plug-import-completion). Reading Events is not a third. Note how `GetMeterReadingHistory` deliberately uses `FindMainMeterByHouseholdAsync` rather than `GetOrCreate…` so that *viewing* history can never create a row — hold this read path to the same standard.

### Carry forward 6.1's error-code contract (AD-18)

6.1's code review added a machine-readable `errorCode` to every Event exception, emitted as a ProblemDetails extension and mapped by the SPA to `event.errors.*`. The client never renders the server's English `detail`. Any new failure mode here follows that contract — this is why Task 3.2 forbids a bare `Results.Problem` and Task 4.2 extracts the mapper rather than letting it be copied.

### Pagination

Default `pageSize` 20, max 100, matching the Meter Readings list. Note the expected characteristic: with reverse-chronological ordering, a newly logged Event shifts rows across page boundaries mid-browse. That matches the existing Meter Readings list and is not a defect.

### Testing rules and verification

- **.NET:** `{SubjectClass}Tests` naming, `Snake_case_with_underscores` methods, Shouldly, NSubstitute against `Application/Ports`, `TestContext.Current.CancellationToken`.
- **Frontend:** colocated Vitest + Testing Library, `jsdom`, globals on.
- **Definition of done runs clean:** `dotnet build`; `dotnet test` for each of Application/Api/Infrastructure/Architecture (each project separately — the MTP runner rejects multiple project paths in one invocation); `npx vitest run`; `npx tsc -b`; `npx oxlint`; `npx vite build`. Infrastructure tests need Docker running (Testcontainers spins real Postgres **and** SQL Server).

### Project Structure Notes

Extends existing files wherever possible (`IEventRepository`, `EventRepository`, `EventEndpoints`, `event-api.ts`, `EventRepositoryTests`, `log-event-sheet.tsx`, `trend-history-page.tsx` + its test) rather than creating parallel ones. Genuinely new files: `GetEventHistory.cs`, `GetEventHistoryTests.cs`, `events-card.tsx`, `events-card.test.tsx`, and the two migration files. No conflicts with existing structure detected.

### Story sequencing note

Inserted between 6.1 and the Wattage Plausibility Correlation story (renumbered 6.2 → **6.3**) during 6.1's code review, once it emerged that 6.3's ACs already presuppose a surface that displays Events. `epic-6-*.md` and `sprint-status.yaml` were updated; two dated historical documents still say "6.2" for the correlation story and were deliberately left as point-in-time records.

## Dev Agent Record

### Agent Model Used

### Debug Log References

### Completion Notes List

### File List

## Change Log

- 2026-09-18: Story created (create-story), inserted as 6.2 ahead of the renumbered 6.3 Wattage Plausibility Correlation. Originates from Story 6.1's code review, which ruled AC #4's display half unsatisfied and deferred the Event read path plus its time-ordered index to a follow-up story.

## References

- [Source: _bmad-artifacts/planning/epics/epic-6-context-capture-wattage-plausibility.md#Story 6.2: Event History View] — ACs, FR-16.
- [Source: _bmad-artifacts/implementation/6-1-event-logging.md#Review Findings] — the AC #4 display-half ruling that produced this story, and the item Task 6 closes.
- [Source: _bmad-artifacts/implementation/deferred-work.md#Deferred from: code review of story-6.1 (2026-09-18)] — the missing time-ordered index, deferred to "whichever story introduces the Event read path" (Task 2).
- [Source: _bmad-artifacts/planning/architecture/architecture-energy-tracker-2026-08-09/ARCHITECTURE-SPINE/invariants-rules.md#AD-3] — tenant isolation via the global query filter.
- [Source: _bmad-artifacts/planning/architecture/architecture-energy-tracker-2026-08-09/ARCHITECTURE-SPINE/invariants-rules.md:161] — the AD-10 "never re-derive the snapshot" constraint bullet (inside the AD-22 section).
- [Source: _bmad-artifacts/planning/architecture/architecture-energy-tracker-2026-08-09/ARCHITECTURE-SPINE/invariants-rules.md#AD-14] — Main Meter sole-authoritative-total isolation.
- [Source: _bmad-artifacts/planning/ux-designs/ux-energy-tracker-2026-08-08/EXPERIENCE.md] — Trend History's card composition and order, collapsed-disclosure idiom (NFR15), quiet-voice empty-state tone.
- [Source: src/EnergyTracker.Application/GetMeterReadingHistory.cs] — paged read use-case shape, the three pagination guards, and the no-side-effect read precedent.
- [Source: src/EnergyTracker.Api/Endpoints/MeterReadingEndpoints.cs] — `MapGet` parameter shape, page/pageSize defaults, `ToHistoryPageResponse` naming.
- [Source: src/EnergyTracker.Api/Endpoints/EventEndpoints.cs] — the existing `Problem(detail, errorCode, statusCode)` helper, `TryGetHouseholdId`, and `EventResponse` to reuse.
- [Source: src/EnergyTracker.Infrastructure/Configurations/MeterReadingConfiguration.cs] — composite-index precedent and why it is not a reason to keep the redundant single-column index here.
- [Source: web/src/components/meter-reading/meter-readings-card.tsx] — card idiom: `GlassCard` + collapsed `details`, `role="heading"` summary, `PAGE_SIZE`, locale date formatting.
- [Source: web/src/components/trend-history/trend-history-page.tsx] — mount point (lines 85-87) and the card-order header comment to update.
- [Source: web/src/components/trend-history/trend-history-page.test.tsx] — the `mockRoutes` allowlist and ordering test that Task 5.5 must update.
- [Source: web/src/lib/event-api.ts, web/src/components/event/log-event-sheet.tsx] — the fetch layer to extend and the `messageFor` mapper to relocate.
- [Source: tests/EnergyTracker.Infrastructure.Tests/EventRepositoryTests.cs] — the abstract base/provider-subclass structure new repository tests must extend.
- [Source: docs/local-development.md] — running the stack for Task 7's live verification.
- [Source: _bmad-artifacts/project-context.md] — AD-2 migration rule, dual-provider testing, i18n parity, naming and testing conventions.
