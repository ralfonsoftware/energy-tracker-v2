---
baseline_commit: ce0cf5a8714ef5d589fbb05cbd3d5722cd684957
---

# Story 6.2: Event History View

Status: done

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

- [x] Task 1: Read path — Application + Infrastructure (AC: #1, #2)
  - [x] 1.1 Extend `src/EnergyTracker.Application/Ports/IEventRepository.cs`:
    ```csharp
    Task<(IReadOnlyList<Event> Items, int TotalCount)> GetPageForHouseholdAsync(
        int page, int pageSize, CancellationToken cancellationToken);
    ```
    **No `householdId` parameter** — AD-3's DbContext query filter already scopes the read, exactly as `ITaggingScaffoldRepository.List*Async` does (that port's doc comment says so outright).
  - [x] 1.2 Implement in `src/EnergyTracker.Infrastructure/Adapters/EventRepository.cs`:
    - Order by `OccurredAt` DESC, then `CreatedAtUtc` DESC as a stable tiebreaker (two Events backfilled to the same `OccurredAt` must not reorder between pages).
    - Get `TotalCount` via `CountAsync` on the same filtered query **before** `Skip`/`Take` — never by materializing the set and counting in memory.
    - Use `AsNoTracking()` — this is a read path.
    - Never `Find()`, `FromSqlRaw`, or `IgnoreQueryFilters()` (project-context names these as the specific AD-3 bypasses).
  - [x] 1.3 Add `src/EnergyTracker.Application/GetEventHistory.cs`. Exact shape — do not re-derive it from `GetMeterReadingHistory`:
    ```csharp
    public record EventHistoryPage(IReadOnlyList<Event> Items, int TotalCount, int Page, int PageSize);

    public class GetEventHistory(IEventRepository repository)
    {
        private const int MaxPageSize = 100;

        public async Task<EventHistoryPage> ExecuteAsync(int page, int pageSize, CancellationToken cancellationToken)
    ```
    **The signature takes no `householdId`** — unlike `GetMeterReadingHistory`, which needs one only to look up a Main Meter. Events have no such parent.
  - [x] 1.4 Copy `GetMeterReadingHistory`'s three guards verbatim, including the overflow guard — it exists because `Skip((page - 1) * pageSize)` overflows int32 for an absurd page, and is checked in long arithmetic so the check itself cannot overflow:
    ```csharp
    if (page < 1) throw new EventValidationException($"page must be at least 1, got '{page}'.", "event.page_invalid");
    if (pageSize < 1 || pageSize > MaxPageSize) throw new EventValidationException($"pageSize must be between 1 and {MaxPageSize}, got '{pageSize}'.", "event.page_size_invalid");
    if ((long)(page - 1) * pageSize > int.MaxValue) throw new EventValidationException($"page {page} is out of range for pageSize {pageSize}.", "event.page_invalid");
    ```
    Reuse the existing two-argument `EventValidationException(message, errorCode)` from 6.1 — do not add a new exception type and do not add a message-only overload.
- [x] Task 2: Index migration (AC: #1)
  - [x] 2.1 In `src/EnergyTracker.Infrastructure/Configurations/EventConfiguration.cs`, **replace** `HasIndex(e => e.HouseholdId)` with `HasIndex(e => new { e.HouseholdId, e.OccurredAt, e.CreatedAtUtc })`. The single-column index becomes strictly redundant once the composite leads with the same column, and the third column covers the tiebreaker so the sort is fully index-ordered on both providers. (`MeterReading` keeps both only because its composite leads with `MainMeterId`, a *different* column — not a precedent for keeping both here.)
  - [x] 2.2 Add the migration to **both** provider projects via `scripts/add-migration.sh AddEventOccurredAtIndex` (AD-2 — never `dotnet ef migrations add` directly). Confirm the generated migration includes the DROP of the old single-column index on both providers.
- [x] Task 3: API endpoint (AC: #1)
  - [x] 3.1 Add `api.MapGet("/events", ...)` to the **existing** `src/EnergyTracker.Api/Endpoints/EventEndpoints.cs` — do not create a second endpoints file. Mirror `MeterReadingEndpoints.MapGet("/meter-readings")`'s **parameter shape and 403 gate** (`int page = 1, int pageSize = 20`, file-local `TryGetHouseholdId`). The error path deliberately diverges — see 3.2.
  - [x] 3.2 Call `TryGetHouseholdId(householdAccessor, out _, out var forbidden)` purely as the **auth gate** (403 when the principal has no Household); the id itself is discarded, because AD-3's filter does the scoping. Catch `EventValidationException` and return it through the existing private `Problem(detail, errorCode, statusCode)` helper so the response carries the `errorCode` extension the SPA localizes. **Do not call `Results.Problem` directly** — `MeterReadingEndpoints` does, and that predates 6.1's error-code contract; copying it would regress a fix that shipped days ago.
  - [x] 3.3 Reuse the existing `EventResponse`; add `EventHistoryPageResponse(IReadOnlyList<EventResponse> Items, int TotalCount, int Page, int PageSize)` and a private `ToHistoryPageResponse` mapper (naming follows `MeterReadingHistoryPageResponse`/`ToHistoryPageResponse`). Register `GetEventHistory` in `Program.cs` beside the existing `AddScoped<CreateEvent>()`.
- [x] Task 4: Frontend Event history card (AC: #1–#6)
  - [x] 4.1 Extend `web/src/lib/event-api.ts` with `fetchEventHistory(page, pageSize): Promise<EventHistoryPageDto>` (DTO named for symmetry with `MeterReadingHistoryPageDto`). Keep the file's existing local `ApiError`/`toApiError` — per this repo's convention these are copied per API file, not shared (see the comment at the top of `tariff-api.ts`).
  - [x] 4.2 **Move** the `errorCode` → catalog mapping out of `log-event-sheet.tsx` (its `messageFor` helper, lines ~95-107: strip the `^event\.` prefix, look up `event.errors.<code>`, fall back to `event.errorGeneric`, special-case 401 → `event.errors.sessionExpired`) into a shared exported helper in `event-api.ts`, and update the sheet to use it. Both surfaces need identical behavior; duplicating it invites one of them to drift back to rendering `err.detail`, the English text AD-18 forbids.
  - [x] 4.3 Add `web/src/components/event/events-card.tsx`, modelled on `web/src/components/meter-reading/meter-readings-card.tsx`. Match that card's idiom precisely, because all three Trend History cards share it:
    - `GlassCard` wrapping `<details className="group">`, **collapsed by default** (NFR15's "says less, on purpose").
    - `<summary>` containing `<span role="heading" aria-level={2}>` with the summary label, so screen readers can navigate to the card without disturbing the summary's toggle semantics.
    - `const PAGE_SIZE = 20` as a module constant; a `locale: string` prop formatted via `new Intl.DateTimeFormat(locale, { dateStyle: 'medium', timeStyle: 'short' })` for `OccurredAt` (AD-18 — locale drives all date formatting).
    - Same loading / empty / error handling and pagination affordance.
  - [x] 4.4 Render the tag as plain text from `taggedEntityName`. **Never** fetch `/api/rooms`, `/api/power-points`, or `/api/devices` to resolve or verify it, and never branch on whether the tagged entity still exists (AD-10 — see Dev Notes).
  - [x] 4.5 Mount it in `web/src/components/trend-history/trend-history-page.tsx` between `<MeterReadingsCard/>` and `<PerPlugDataCard/>` (currently lines 85-87). **Update that file's header comment**, which documents the three-card order and its rationale ("Card order: chart, then Meter Readings … the tree stays last") — a fourth card silently invalidates it.
  - [x] 4.6 i18n (AD-18 — both catalogs, same commit, identical key sets): card copy goes under **`trendHistory.eventsCard.*`** (`summary`/`expand`/`collapse`/empty/error), matching its two sibling cards — *not* under `event.*`. The new pagination error codes **must** go under `event.errors.page_invalid` / `event.errors.page_size_invalid`, because the shared mapper from 4.2 resolves that prefix.
- [x] Task 5: Tests
  - [x] 5.1 `GetEventHistoryTests.cs` (Application.Tests): reverse-chronological ordering by `OccurredAt`; the `CreatedAtUtc` tiebreaker for equal `OccurredAt`; each pagination guard throws `EventValidationException` with the expected `ErrorCode`; an empty Household returns an empty page rather than throwing.
  - [x] 5.2 `EventRepositoryTests.cs` (Infrastructure.Tests): add tests **to the abstract `EventRepositoryTestsBase`**, not to a provider subclass — the `PostgresEventRepositoryTests`/`SqlServerEventRepositoryTests` subclasses already exist and inherit automatically, so adding to one subclass silently halves AD-2 coverage. Cover ordering, paging boundaries, and that the AD-3 filter excludes another Household's Events from the page.
  - [x] 5.3 `EventEndpointsTests.cs` (Api.Tests): 200 with the page shape; 400 carrying an `errorCode` for a bad `page`/`pageSize`; 403 when the caller has no Household; and an Event whose tag was archived after logging still returning its original `taggedEntityName` (AC #3, end-to-end).
  - [x] 5.4 `events-card.test.tsx` (Vitest): renders a tagged and an untagged Event; empty state; error state; and — the AD-10 guard — renders the tag of an Event whose entity was archived while **asserting no request was made to `/api/rooms`, `/api/power-points`, or `/api/devices`**. Assert on the fetch stub, not just the DOM: a DOM-only assertion still passes if someone adds a resolve-then-render path that happens to return the same name.
  - [x] 5.5 Update `web/src/components/trend-history/trend-history-page.test.tsx` — **this file will break otherwise.** Its `mockRoutes` helper allowlists `/api/status/history`, `/api/meter-readings`, `/api/smart-plug-readings` and falls through to `jsonResponse(null)`, so an unhandled `/api/events` returns 200 with a null body, `response.json()` throws, and the new card renders its error state inside *every* existing test. Add an `/api/events` branch to both fetch stubs in that file, and extend the ordering test (`'renders the chart, the Meter Readings card, and the Per-Plug card in that order'`, line 34 — it asserts document position) to include the Events card.
- [x] Task 6: Close out Story 6.1 (AC: #3)
  - [x] 6.1 In `_bmad-artifacts/implementation/6-1-event-logging.md`, tick the AC #4 decision item under `### Review Findings` — the one reading "This AC closes only when the Event read path ships in the follow-up story" — and note that 6.2 closed it.
  - [x] 6.2 Set that story's `Status:` to `done` and add a Change Log line, **provided** its live-verification gate remains satisfied and no other review item is open.
  - [x] 6.3 Set `6-1-event-logging: done` in `sprint-status.yaml` with a dated note. Story 6.1 is currently `in-progress` blocked on exactly this AC — without this task it stays blocked indefinitely.
- [x] Task 7: Live verification (AC: #3, #5)
  - [x] 7.1 This project's standing gate requires a live Auth0/Chrome check before review→done; it does not defer. AC #3 is a *display* behavior, which unit tests can only simulate — and 6.1's own review found a real layout defect only once a live render was run.
  - [x] 7.2 Run the stack per `docs/local-development.md` (Postgres via compose → `./scripts/migrate.sh` → `./scripts/run-api.sh` → `npm --prefix web run dev`; Vite serves **HTTPS** on 5173, and Chrome is the supported browser for plain-localhost cookie behavior). Then, signed in: log an Event with a tag → archive that Room/Power Point/Device → reload Trend History → confirm the tag still reads as plain text with no error and no "deleted" styling. Confirm the empty state on a Household with no Events. Record the outcome in the Dev Agent Record.

### Review Findings

Code review run 2026-09-19 (3 parallel layers: Blind Hunter, Edge Case Hunter, Acceptance Auditor). Blind Hunter independently re-verified: `dotnet build` clean, `EnergyTracker.Application.Tests` 325/325, `EnergyTracker.Api.Tests` 201/201, both touched Vitest files 12/12, en-US/de-DE key parity confirmed. Findings below were read against the real source, not accepted from a review layer on assertion alone.

**Decision needed**

- [x] [Review][Decision] **RESOLVED — live-verified during code review, 2026-09-19.** AC #5's empty state was never live-verified at dev-story time, contradicting Task 7.1's own "does not defer" gate — Task 7 and 7.2 were checked off complete anyway on the strength of the component test alone. Resolved by performing the live check in this review session: the shared test household's single Event row was captured, temporarily deleted, the empty state confirmed live in Chrome ("No Events logged yet.", clean console, `GET /api/events` → 200), and the row restored byte-for-byte. See the Dev Agent Record's new "Code review live verification" entry for full detail. Gate cleared; `done` is now unblocked on this point.

**Patch**

- [x] [Review][Patch] Pagination controls render alongside the error message when a page ≥2 fetch fails after an earlier successful fetch — the gate at `events-card.tsx:100` checks only `data && data.totalCount > 0`, not `!error`, so stale `data` from the prior success keeps the Previous/Next controls (and stale `totalPages`) visible under the error text. **Fixed:** gate now also requires `!error`. [web/src/components/event/events-card.tsx:100]
- [x] [Review][Patch] `EventRow` treats an empty-string `taggedEntityName` identically to no tag (`item.taggedEntityName &&` at line 129) — nothing in the domain/schema currently guarantees a non-empty snapshot, so a tagged Event could silently render as untagged with no indication anything was dropped. **Fixed:** replaced the truthy check with an explicit `!= null` check. [web/src/components/event/events-card.tsx:129]
- [x] [Review][Patch] `EventRepository.GetPageForHouseholdAsync`'s ordering (`OccurredAt` DESC, `CreatedAtUtc` DESC) has no final tiebreaker — two Events with identical `OccurredAt` *and* `CreatedAtUtc` have undefined relative order, which can flip between page requests and duplicate or skip a row across pages. **Fixed:** added `.ThenByDescending(e => e.Id)`. [src/EnergyTracker.Infrastructure/Adapters/EventRepository.cs:32]

All 3 patches verified: `dotnet build` clean; `EnergyTracker.Application.Tests` 325/325; `EnergyTracker.Infrastructure.Tests` 188/188 (Testcontainers, both providers); `EnergyTracker.Api.Tests` 201/201; affected Vitest files 12/12.

**Defer**

- [x] [Review][Defer] Non-numeric `page`/`pageSize` query values (e.g. `?page=abc`) fail ASP.NET's minimal-API model binding before `GetEventHistory`'s own guards run, returning a generic ProblemDetails with no `errorCode` extension — the frontend's `messageForEventError` then falls back to a generic message. Pre-existing pattern, identical in `MeterReadingEndpoints` (this story's own "match precisely" instruction), not introduced here. [src/EnergyTracker.Api/Endpoints/EventEndpoints.cs:61] — deferred, pre-existing
- [x] [Review][Defer] `GetPageForHouseholdAsync`'s `CountAsync` and `Skip`/`Take` are two separate round trips with no transaction/snapshot isolation — a concurrent insert between them can make `TotalCount` and the returned page briefly inconsistent. Standard pattern, mirrors the existing `GetMeterReadingHistory` precedent. [src/EnergyTracker.Infrastructure/Adapters/EventRepository.cs:30] — deferred, pre-existing
- [x] [Review][Defer] `EventsCard` eager-fetches on mount regardless of whether the (collapsed-by-default) details element is ever expanded, matching `MeterReadingsCard`'s existing idiom (this story's Task 4.3 explicitly says to match it precisely). This is now the 3rd disclosure card doing this on Trend History, so the page now issues 3 collection fetches unconditionally on every visit. [web/src/components/trend-history/trend-history-page.tsx] — deferred, pre-existing
- [x] [Review][Defer] AC #3's "still displays after archive" automated coverage (`EventRepositoryTests`, `EventEndpointsTests`) only exercises the `Room` tag type — `PowerPoint`/`Device` are only covered for the creation-time archived-rejection path (409), not the persists-after-archive-at-display-time path. Functional risk is low since the display code path never branches on tag type, but a parametrized test across all 3 types would close the gap. [tests/EnergyTracker.Infrastructure.Tests/EventRepositoryTests.cs, tests/EnergyTracker.Api.Tests/EventEndpointsTests.cs] — deferred, follow-up test coverage
- [x] [Review][Defer] `EventsCard`'s `page` state has no guard against landing out-of-range if `totalCount` shrinks below the current page (e.g. once an Event-delete feature ships, or another tab mutates data while this card sits open on page 2+) — not reachable today since no delete affordance exists yet. [web/src/components/event/events-card.tsx:18] — deferred, not reachable today
- [x] [Review][Defer] Story 6.1's status flip to `done` (Task 6) is bundled into the same diff as 6.2's own new feature code, coupling the two stories' lifecycle state — if 6.2 needs a substantial revert post-review, 6.1 would revert alongside it even though none of 6.1's already-shipped code changed here. — deferred, process/documentation observation

**Dismissed as noise (4):** `EventsCard`'s error-message specificity diverging from `MeterReadingsCard`'s "match precisely" instruction (unreachable in practice — `page`/`pageSize` are always caller-controlled valid ints from this UI, and the added specificity is strictly more informative, not a regression); `GetEventHistoryTests`' `CreatedAtUtc` tiebreak test not exercising real tiebreak logic (its own inline comment says so explicitly, and the real tiebreak is correctly covered at the `EventRepositoryTests` layer); the story's self-reported test counts (Blind Hunter independently re-ran and matched Application/Api/Vitest counts exactly — only the Testcontainers-based Infrastructure suite and full frontend suite remain unverified, which is expected review scope); the two migration `*.Designer.cs` partner files being absent from the reviewed diff (an artifact of how this review's diff was constructed — excluded as auto-generated boilerplate — content separately verified on disk to match the intended composite index).

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

Claude Sonnet 5 (claude-sonnet-5)

### Debug Log References

None — no debugging required beyond the standard red-green-refactor cycle.

### Completion Notes List

- All 7 tasks complete, all 6 ACs satisfied.
- Backend: `IEventRepository.GetPageForHouseholdAsync` and `GetEventHistory` follow `GetMeterReadingHistory`'s shape exactly (no `householdId` param — AD-3's query filter scopes the read; the same three pagination guards). `EventRepository`'s implementation orders by `OccurredAt` DESC then `CreatedAtUtc` DESC, gets `TotalCount` via `CountAsync` before `Skip`/`Take`, and uses `AsNoTracking()`.
- `EventConfiguration`'s single-column `HouseholdId` index was replaced with a composite `(HouseholdId, OccurredAt, CreatedAtUtc)` index; the `AddEventOccurredAtIndex` migration (added via `scripts/add-migration.sh` to both provider projects) drops the old index and creates the composite on both Postgres and SQL Server.
- `GET /api/events` mirrors `MeterReadingEndpoints`'s parameter shape (`page`/`pageSize` defaults) but keeps 6.1's `errorCode`-carrying `Problem()` helper rather than a bare `Results.Problem`, per Dev Notes.
- Frontend: `events-card.tsx` is modeled directly on `meter-readings-card.tsx` (GlassCard + collapsed `details`, `role="heading"` summary, locale date formatting, pagination). It renders `taggedEntityName` as plain text with no fetch to `/api/rooms`/`/api/power-points`/`/api/devices` and no "still exists?" branch (AD-10). The `errorCode` → catalog mapping was moved out of `log-event-sheet.tsx`'s inline `messageFor` into a shared `messageForEventError` export in `event-api.ts`, used by both surfaces now.
- Mounted `<EventsCard/>` between `<MeterReadingsCard/>` and `<PerPlugDataCard/>` in `trend-history-page.tsx`; updated that file's card-order header comment. i18n additions (`trendHistory.eventsCard.*`, `event.errors.page_invalid`/`page_size_invalid`) added to both `en-US` and `de-DE` catalogs with identical key sets (verified programmatically — zero drift).
- Task 6: closed Story 6.1's AC #4 decision item (its display half), set 6.1's Status to `done`, and updated `sprint-status.yaml` accordingly — no other review item was open on 6.1.
- **Task 7 live verification (2026-09-19, Chrome against the real Auth0 test-user session, Postgres + API + Vite running locally via `docs/local-development.md`):**
  - Logged an Event ("Story 6.2 live verification: away 2 weeks") tagged to the "Living Room" Room via the existing Log Event sheet — `POST /api/events` succeeded, confirmation rendered.
  - Opened Trend History: the new "Events — 1 logged" card renders between Meter Readings and the Room → Power Point → Device tree, exactly per the mount-order Dev Note. Expanded it: the row shows the description, the "Living Room" tag as plain text, and the formatted date/time; pagination controls present.
  - Archived the "Living Room" Room via Settings, then reloaded Trend History: the Event row still reads "Living Room" as plain text — **no error, no broken reference, no "(deleted)" styling** (AC #3 confirmed live). No console errors (`read_console_messages`, `onlyErrors: true`, clean).
  - Confirmed via `read_network_requests` that opening the Events card fired only `GET /api/events?page=1&pageSize=20` (200) — no request to `/api/rooms`, `/api/power-points`, or `/api/devices` was made, confirming AD-10 holds in a real render, not just in the mocked component test.
  - **AC #5 (empty state) was verified via the automated component test (`events-card.test.tsx`), not live** at dev-story time — the shared Auth0 test-user household already carried data from this and prior stories' live verifications (e.g. Story 6.1, Story 1.12), and there's no in-app way to reset just the Event list without discarding that other verified state. **Closed during code review, 2026-09-19** — see "Code review live verification" below.
  - Not covered: a cold sign-in through the Auth0 login form — an existing valid session from a prior story's verification was already active in the browser profile.
  - Test data left in place afterward: the "Story 6.2 live verification" Event (Events are append-only by design — no delete path exists) and the archived "Living Room" Room (no restore/unarchive capability exists in the app). Both are expected, inert local-dev state, not a cleanup gap.
- Full verification: `dotnet build` clean; `dotnet test` per project — Application 325/325, Infrastructure 188/188 (Testcontainers, both providers), Api 201/201, Architecture 4/4; `npx vitest run` 369/369; `npx tsc -b` clean; `npx oxlint` shows only pre-existing warnings in untouched files; `npx vite build` clean.
- **Code review live verification (2026-09-19, Chrome against the real Auth0 test-user session, stack running per `docs/local-development.md`):** AC #5's empty state, deferred at dev-story time, was live-verified during code review by temporarily removing the shared test household's single Event row directly in Postgres (full row captured first: `Id=973db97c-efe0-47af-a328-bccfdea24219`), confirming the live render, then restoring the identical row immediately after — a reversible local-dev-only DB toggle, approved by the user in the review session. With the household's Event list empty, Trend History's "Events" card showed "Events — 0 logged" in the summary and, expanded, rendered "No Events logged yet." in the product's established quiet voice — no error, no broken layout. `read_console_messages` (`onlyErrors: true`) was clean. `read_network_requests` confirmed `GET /api/events?page=1&pageSize=20` → `200`. The row was then re-inserted with identical column values and verified byte-for-byte against the pre-deletion snapshot. Gate cleared.

### File List

**Backend — new:**
- `src/EnergyTracker.Application/GetEventHistory.cs`
- `src/EnergyTracker.Infrastructure.Migrations.Postgres/Migrations/20260919075303_AddEventOccurredAtIndex.cs` (+ `.Designer.cs`)
- `src/EnergyTracker.Infrastructure.Migrations.SqlServer/Migrations/20260919075306_AddEventOccurredAtIndex.cs` (+ `.Designer.cs`)

**Backend — modified:**
- `src/EnergyTracker.Application/Ports/IEventRepository.cs`
- `src/EnergyTracker.Infrastructure/Adapters/EventRepository.cs`
- `src/EnergyTracker.Infrastructure/Configurations/EventConfiguration.cs`
- `src/EnergyTracker.Infrastructure.Migrations.Postgres/Migrations/EnergyTrackerDbContextModelSnapshot.cs`
- `src/EnergyTracker.Infrastructure.Migrations.SqlServer/Migrations/EnergyTrackerDbContextModelSnapshot.cs`
- `src/EnergyTracker.Api/Endpoints/EventEndpoints.cs`
- `src/EnergyTracker.Api/Program.cs`

**Frontend — new:**
- `web/src/components/event/events-card.tsx`
- `web/src/components/event/events-card.test.tsx`

**Frontend — modified:**
- `web/src/lib/event-api.ts`
- `web/src/components/event/log-event-sheet.tsx`
- `web/src/components/trend-history/trend-history-page.tsx`
- `web/src/components/trend-history/trend-history-page.test.tsx`
- `web/src/locales/en-US/translation.json`
- `web/src/locales/de-DE/translation.json`

**Tests — new:**
- `tests/EnergyTracker.Application.Tests/GetEventHistoryTests.cs`

**Tests — modified:**
- `tests/EnergyTracker.Infrastructure.Tests/EventRepositoryTests.cs`
- `tests/EnergyTracker.Api.Tests/EventEndpointsTests.cs`

**Other stories/docs updated (Task 6):**
- `_bmad-artifacts/implementation/6-1-event-logging.md` (Status -> done, Review Findings, Change Log)
- `_bmad-artifacts/implementation/sprint-status.yaml`

## Change Log

- 2026-09-19: Code review complete (3 parallel layers). AC #5's deferred live verification was resolved in-session (shared test household's Event row temporarily removed and restored, confirming the empty state live). 3 patches applied and verified (pagination/error-state UI gate, empty-string `taggedEntityName` guard, `EventRepository` ordering tiebreaker); 6 items deferred to `deferred-work.md`; 4 dismissed as noise. All suites re-verified green after patches. Status set to done.
- 2026-09-19: Story implemented (dev-story). All 7 tasks complete, all 6 ACs satisfied. Full backend suite green (325 Application + 188 Infrastructure + 201 Api + 4 Architecture), full frontend suite green (369/369), `dotnet build`/`tsc -b`/`oxlint`/`vite build` all clean. Live Auth0/Chrome verification performed: AC #3 (tag survives archiving the tagged Room, live, with a network-request check confirming no scaffold-endpoint calls) confirmed; AC #5 (empty state) verified via component test rather than live, since the shared test household already carries other stories' verified data. Closed Story 6.1's AC #4 display-half decision item and moved 6.1 to done. Status set to review.
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
