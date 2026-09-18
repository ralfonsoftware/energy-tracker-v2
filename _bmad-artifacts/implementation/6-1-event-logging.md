---
baseline_commit: ce0cf5a8714ef5d589fbb05cbd3d5722cd684957
---

# Story 6.1: Event Logging

Status: in-progress

<!-- Note: Validation is optional. Run validate-create-story for quality check before dev-story. -->

## Story

As a Household member,
I want to log a short text/tap-first Event with an optional tag,
so that I have a fast way to note the unmeasurable things — like the induction cooktop or a long trip — that might explain a consumption change.

## Acceptance Criteria

1. **Given** the Log Event surface, **when** I enter a short text/tap-first Event (e.g. "cooked 2h," "away 2 weeks"), **then** logging it takes comparable effort to a Meter Reading entry — not a form (FR-16).
2. **Given** the Event entry, **when** I optionally tag it to a Room, Power Point, or Device, **then** the tag is recorded alongside the Event.
3. **Given** a past date/time, **when** I log an Event for it, **then** it's accepted as a backfill, same as a Meter Reading (FR-16).
4. **Given** a Room/Power Point/Device tagged on an Event, **when** the tagged item is later soft-deleted (AD-10), **then** the Event's historical tag remains inert display text rather than a broken reference (FR-16).
5. **Given** a household's use of Events over time, **when** observed, **then** each Event is a single dated occurrence — there is no recurring/pattern-event mechanism in v2 (FR-16, Out of Scope).

## Tasks / Subtasks

- [x] Task 1: Domain entity + EF configuration (AC: #2, #4)
  - [x] 1.1 Add `src/EnergyTracker.Domain/Event.cs` — see Dev Notes' exact shape (resolved-ambiguity tag model).
  - [x] 1.2 Add `src/EnergyTracker.Infrastructure/Configurations/EventConfiguration.cs` (mirror `MeterReadingConfiguration.cs`'s shape/comments pattern).
  - [x] 1.3 Add `DbSet<Event> Events => Set<Event>();` and the AD-3 query filter (`modelBuilder.Entity<Event>().HasQueryFilter(e => e.HouseholdId == CurrentHouseholdId);`) in `EnergyTrackerDbContext.cs`.
  - [x] 1.4 Add the migration to **both** provider projects via `scripts/add-migration.sh AddEvent` (AD-2 — never `dotnet ef migrations add` directly).
- [x] Task 2: Application layer (AC: #1, #2, #3, #5)
  - [x] 2.1 Add `src/EnergyTracker.Application/Ports/IEventRepository.cs`.
  - [x] 2.2 Add `src/EnergyTracker.Infrastructure/Adapters/EventRepository.cs`.
  - [x] 2.3 Add `src/EnergyTracker.Application/CreateEvent.cs` — validates Description (non-blank, capped length), validates the tag (if any) resolves to a live, non-archived Room/PowerPoint/Device in the caller's Household before snapshotting its name, applies the same future/past timestamp bounds as `CreateMeterReading`.
  - [x] 2.4 Add `EventValidationException.cs` (mirror `MeterReadingValidationException.cs`, for blank/over-length Description or an out-of-range timestamp — maps to 400). Add a second, distinct `EventTaggedEntityArchivedException.cs` for "tag target already archived" — this is a **409**, not a 400, matching `TaggingScaffoldParentArchivedException`'s established convention for the same class of error (a race where the client's view is stale), not `MeterReadingValidationException`'s 400 convention.
- [x] Task 3: API endpoint (AC: #1, #2, #3)
  - [x] 3.1 Add `src/EnergyTracker.Api/Endpoints/EventEndpoints.cs` — `POST /api/events` only (no GET/list — nothing in Epic 6 needs a history view yet; don't build one speculatively). Catch `EventValidationException` → 400, `EventTaggedEntityArchivedException` → 409 (see Dev Notes).
  - [x] 3.2 Register `IEventRepository`, `CreateEvent`, and `MapEventEndpoints()` in `Program.cs` — same three-line shape as `MeterReading`'s registrations (`builder.Services.AddScoped<IMeterReadingRepository, MeterReadingRepository>()` / `AddScoped<CreateMeterReading>()` / `api.MapMeterReadingEndpoints()`), just one use case instead of three.
- [x] Task 4: Frontend Log Event sheet (AC: #1, #2, #3)
  - [x] 4.1 Add `web/src/components/event/log-event-sheet.tsx` — reuse `LogReadingSheet`'s exact shape (bottom `Sheet`, `GLASS_SHEET_CLASSNAME`, `glass-primary` submit button): a single-line free-text `Input` (no `Textarea` primitive exists in `web/src/components/ui` yet — don't add one; a single line matches AC #1's "comparable effort to a Meter Reading entry" bar better than a multi-line box invites), a datetime-local backfill field defaulting to now (copy `toDateTimeLocalValue` from `log-reading-sheet.tsx`), and an optional tag picker.
  - [x] 4.2 Tag picker: fetch `/api/rooms`, `/api/power-points`, `/api/devices` (same three parallel GETs as `TaggingScaffoldManager`), filter to non-archived, and let the user pick at most one Room, Power Point, or Device (flat list, `Room → PowerPoint` label style from `MoveDestinationList`'s `getLabel` pattern). No tag is a valid choice.
  - [x] 4.3 Wire a plain `fetch('/api/events', { method: 'POST', credentials: 'include', ... })` — **do not** route this through `attemptSend`/the offline queue (see Dev Notes' "No offline queue" warning).
  - [x] 4.4 Add a topbar icon-button entry point on `DashboardPage` (mirror `onSmartPlugImportClick`'s exact button/icon markup, lift `logEventOpen`/`onLogEventOpenChange` state into `App.tsx` exactly like `logSheetOpen`).
  - [x] 4.5 Add i18n keys to **both** `web/src/locales/en-US/translation.json` and `web/src/locales/de-DE/translation.json` (AD-18 — a locale addition without both catalogs updated together is a partial i18n rollout).
- [x] Task 5: Tests
  - [x] 5.1 `CreateEventTests.cs` (Application.Tests): blank/over-length Description throws `EventValidationException`; backfill accepted; future-timestamp-beyond-skew throws `EventValidationException`; tagging an archived Room/PowerPoint/Device throws `EventTaggedEntityArchivedException`; tagging a live one snapshots its current name; untagged Event persists with `TaggedEntityType`/`TaggedEntityId`/`TaggedEntityName` all null.
  - [x] 5.2 `EventRepositoryTests.cs` (Infrastructure.Tests, real DbContext — see Dev Notes' EF identity-map warning): confirms the snapshot survives a later rename/archive/re-parent of the tagged item (AC #4) — this specific behavior cannot be verified against a mocked repository.
  - [x] 5.3 `EventEndpointsTests.cs` (Api.Tests): 200 on valid create, 400 on blank/over-length Description, 403 when caller has no Household, 409 when tagging an already-archived Room/PowerPoint/Device.
  - [x] 5.4 `log-event-sheet.test.tsx` (Vitest + Testing Library): submits without a tag, submits with a tag, rejects blank text, backfill date/time round-trips.

## Dev Notes

### Resolved ambiguity: tag data model

Epic 6's ERD (`structural-seed.md`) sketches three separate optional relations (Event→Room, Event→PowerPoint, Event→Device), but this story implements a **single discriminator + id pair** instead of three nullable FK columns:

```csharp
// EnergyTracker.Domain/Event.cs
public class Event
{
    public required Guid Id { get; init; }
    public required Guid HouseholdId { get; init; }
    public required string Description { get; set; }
    public required DateTimeOffset OccurredAt { get; init; }   // user-entered/backfillable — mirrors MeterReading.ReadingTimestamp, not "Utc"-suffixed
    public required DateTimeOffset CreatedAtUtc { get; init; }

    // Null when untagged. "Room" | "PowerPoint" | "Device" — plain discriminator, not an enum,
    // matching AuditCorrection.EntityType's precedent (a 4th taggable type is a data addition,
    // not a schema change). Structurally guarantees "at most one tag type" without an app-level
    // invariant three separate nullable columns would need.
    public string? TaggedEntityType { get; init; }
    public Guid? TaggedEntityId { get; init; }

    // AD-10 by-value snapshot, captured once at write time — never re-derived or overwritten,
    // even after the tagged item is renamed, archived, or re-parented (Story 2.6).
    public string? TaggedEntityName { get; init; }
}
```

Reference: `src/EnergyTracker.Domain/AuditCorrection.cs`'s `EntityType` field is the exact precedent for this choice. `EventConfiguration.cs` should `HasMaxLength(64)` on `TaggedEntityType` (matches `AuditCorrectionConfiguration`'s discriminator column) and `HasMaxLength(500)` on `Description` — `TaggingScaffoldNameValidator.MaxNameLength` (200) is sized for a short entity Name, not a free-text note like "away for 2 weeks visiting family, cooked almost nothing at home the whole time," so don't reuse that constant; 500 is a generous, round cap with no other significance. Validate `Description` non-blank/trimmed the same way `TaggingScaffoldNameValidator` does.

**If this decision needs revisiting:** flag it in code review rather than silently reworking — no other story depends on the physical shape yet, so a change here is still cheap.

### AD-10: tag validation and snapshot (AC #2, #4)

- `CreateEvent.ExecuteAsync` takes an optional `(taggedEntityType, taggedEntityId)` pair. When present:
  1. Resolve the entity via the **existing** `ITaggingScaffoldRepository` (`Application/Ports/ITaggingScaffoldRepository.cs`) — there is one port for the whole Room/PowerPoint/Device scaffold, not three separate repositories. Call `FindRoomAsync`/`FindPowerPointAsync`/`FindDeviceAsync` based on `taggedEntityType`. Do **not** add a new `IRoomRepository`/`IPowerPointRepository`/`IDeviceRepository` — that would duplicate this existing port.
  2. Reject with `EventTaggedEntityArchivedException` (409, not 400 — see Task 2.4) if the entity doesn't exist or is already archived (`ArchivedAt is not null`) — a household should not be able to newly tag something already gone; AC #4 only requires that an *already-tagged* item stays inert display text after a **later** archive, not that a tag can be created against an already-archived one. (Cross-Household access is already impossible here — `FindRoomAsync`/etc. rely on AD-3's DbContext query filter, which scopes every read to the caller's own Household automatically; there's no separate "belongs to a different Household" branch to write.)
  3. Snapshot `TaggedEntityName` from the entity's current `Name` at write time. Never re-read or update it afterward — same discipline `invariants-rules.md`'s AD-10 constraint note calls out for `RoomName`/`PowerPointName` on `SmartPlugReading`: "re-deriving and overwriting them ... would reproduce the exact 'live join rewrites history' failure AD-10 exists to prevent."
- The frontend must never join through `TaggedEntityId` to render the tag — always render `TaggedEntityName` verbatim, including for an Event whose tagged item has since been archived (AC #4's "inert display text").

### Critical guardrail: AD-14 Pattern Detective isolation test

`tests/EnergyTracker.Architecture.Tests/PatternDetectiveDoesNotReferenceSmartPlugOrEventDataTests.cs` source-scans 9 specific files for the literal word `Event` (word-boundary regex, case-sensitive) and **fails the build** if found:

```
src/EnergyTracker.Domain/Calculations/BonusDecayNormalizer.cs
src/EnergyTracker.Domain/Calculations/PatternDetectiveCalculator.cs
src/EnergyTracker.Domain/Status.cs
src/EnergyTracker.Domain/StatusSnapshot.cs
src/EnergyTracker.Application/GetCurrentStatus.cs
src/EnergyTracker.Application/Ports/IStatusRecomputeService.cs
src/EnergyTracker.Infrastructure/Adapters/StatusRecomputeService.cs
src/EnergyTracker.Infrastructure/Configurations/StatusSnapshotConfiguration.cs
src/EnergyTracker.Api/Endpoints/StatusEndpoints.cs
```

**Do not touch any of these files in this story.** This story has no reason to — Event creation never calls `IStatusRecomputeService` (see next point) — but it's called out explicitly because this guard test was written *in anticipation of* the `Event` type existing (it didn't yet, until this story). Confirm the test still passes unmodified after implementation.

### Do NOT call IStatusRecomputeService

AD-7 names exactly two Status-recompute call sites: the Meter-Reading-create handler and the Smart-Plug-import-completion handler. Event creation is **not** a third — logging an Event never triggers a Status recompute. Don't add a call to it "for consistency" with `CreateMeterReading`.

### Do NOT extend the offline queue

`meter-reading-sync.ts`/`offline-queue.ts`'s IndexedDB offline queue is scoped to Meter Reading creation only (project-context.md's Critical Don't-Miss Rules: "don't extend this offline pattern to other writes without a matching architecture decision"). Nothing in Epic 6's ACs or AD-8/AD-10 extends it to Events. The Log Event sheet does a plain `fetch` and shows a normal error state if it fails (offline or otherwise) — it does not queue and silently retry later. If a future story wants offline Event logging, that needs its own architecture decision first.

### No mockup exists yet for the Log Event surface

`EXPERIENCE.md` explicitly says: *"Still spine-only, no rendered mock: Log Event (Epic 6, out of this pass's scope)."* This project's established practice (see `mockups/*.html`) is to build new screens from a rendered HTML reference, not prose alone — a prior spine-only screen (pre-Epic-4 Dashboard/Settings) came out flat and unstyled before that practice was adopted.

This story is lower-risk than that precedent because the surface composes almost entirely from an existing, already-toned analog: `LogReadingSheet` (`web/src/components/meter-reading/log-reading-sheet.tsx`) is explicitly "comparable effort ... not a form" — the exact bar AC #1 sets — and is already built against this project's design tokens (`GLASS_SHEET_CLASSNAME`, `glass-primary` button, `Sheet`/`SheetContent side="bottom"`). Story 3.10's "Clean Up History" control shipped the same way (directly against existing conventions, "rather than waiting on a dedicated UX pass") per `UX-DR21`'s own extension note.

**Reuse `LogReadingSheet`'s visual structure line-for-line**: same `Sheet`/`SheetHeader`/`SheetTitle`/`SheetDescription` skeleton, same `Label` + input pattern, same `glass-primary` submit button, same inline confirmation-text-below-sheet pattern. The one new visual element is the tag picker (Task 4.2) — model it on `MoveDestinationList`'s flat-button-list pattern from `tagging-scaffold-manager.tsx`, not a new combobox component.

If review or Ralf flags a mismatch once built, revisit token/copy choices then — same resolution path `UX-DR21` documented for its own no-mockup addition.

### API/DTO shape reference

Mirror `MeterReadingEndpoints.cs`'s exact pattern: a private `TryGetHouseholdId` helper (copy it — it's private to that file, not shared), a `CreateEventRequest` record, `Results.Problem(..., StatusCodes.Status403Forbidden)` for no-Household, `Results.Problem(..., StatusCodes.Status400BadRequest)` for `EventValidationException`, and `Results.Problem(..., StatusCodes.Status409Conflict)` for `EventTaggedEntityArchivedException`.

```csharp
public record CreateEventRequest(string Description, DateTimeOffset OccurredAt, string? TaggedEntityType, Guid? TaggedEntityId);
public record EventResponse(Guid Id, string Description, DateTimeOffset OccurredAt, string? TaggedEntityType, string? TaggedEntityName);
```

No `IdempotencyKey`/AD-16 concern here — Event creation isn't routed through the offline queue (see above), so there's no retry-after-reconnect scenario that needs upsert-by-key. A plain online `fetch` either succeeds or the user retries manually.

### Timestamp validation

Reuse `CreateMeterReading`'s exact `MaxFutureClockSkew` (5 min) / `MinReadingTimestamp` (2000-01-01) constants and reasoning for `OccurredAt` — same backfill requirement (AC #3), same "obviously wrong client clock" guard.

### Testing Rules

- `.NET`: `{SubjectClass}Tests` naming, `Snake_case_with_underscores` method names, Shouldly assertions, NSubstitute mocks against `Application/Ports` interfaces, `TestContext.Current.CancellationToken`. The AC #4 snapshot-survives-a-later-change test **must** use a real Testcontainers-backed `DbContext`, not a mocked repository — see project-context.md's EF-identity-map warning (Story 5.1's `EditTariff` bug: a mocked repository can't reproduce EF Core returning the same tracked instance across a find-then-update pair, which is exactly the class of bug AD-10's "never re-derive the snapshot" rule guards against).
- Frontend: colocated Vitest + Testing Library, `jsdom`, globals on.

### Project Structure Notes

- Follows the existing flat use-case-per-file Application layer (no feature folders) and `Ports`/`Adapters` split (AD-1).
- New frontend feature folder: `web/src/components/event/` (mirrors `meter-reading/`, `tariff/`, etc. — one folder per feature, per project-context.md).
- No conflicts with existing structure detected.

### References

- [Source: _bmad-artifacts/planning/epics/epic-6-context-capture-wattage-plausibility.md#Story 6.1: Event Logging] — ACs, FR-16.
- [Source: _bmad-artifacts/planning/architecture/architecture-energy-tracker-2026-08-09/ARCHITECTURE-SPINE/invariants-rules.md#AD-10] — soft-delete + by-value snapshot rule.
- [Source: _bmad-artifacts/planning/architecture/architecture-energy-tracker-2026-08-09/ARCHITECTURE-SPINE/invariants-rules.md#AD-14] — Main Meter sole-authoritative-total isolation, and the guard test it produced.
- [Source: _bmad-artifacts/planning/architecture/architecture-energy-tracker-2026-08-09/ARCHITECTURE-SPINE/structural-seed.md#Core Entities (ERD)] — Event's ERD placement (informs, doesn't dictate, the physical schema — see Resolved Ambiguity above).
- [Source: _bmad-artifacts/planning/ux-designs/ux-energy-tracker-2026-08-08/EXPERIENCE.md] — IA row (Log Event, Dashboard/Trend History entry), behavior spec table, no-mock note.
- [Source: src/EnergyTracker.Domain/AuditCorrection.cs] — discriminator-field precedent.
- [Source: src/EnergyTracker.Domain/SmartPlugReading.cs] — by-value snapshot precedent.
- [Source: src/EnergyTracker.Application/CreateMeterReading.cs] — timestamp validation, use-case shape precedent.
- [Source: src/EnergyTracker.Api/Endpoints/MeterReadingEndpoints.cs] — endpoint shape precedent.
- [Source: web/src/components/meter-reading/log-reading-sheet.tsx] — frontend sheet shape precedent.
- [Source: web/src/components/tagging-scaffold/tagging-scaffold-manager.tsx] — tag-target fetch/picker precedent.
- [Source: web/src/App.tsx, web/src/components/dashboard/dashboard-page.tsx] — topbar entry-point + lifted sheet-open-state wiring precedent.
- [Source: tests/EnergyTracker.Architecture.Tests/PatternDetectiveDoesNotReferenceSmartPlugOrEventDataTests.cs] — AD-14 guard test this story must not trip.
- [Source: _bmad-artifacts/project-context.md#Critical Don't-Miss Rules] — offline-queue scope limit, EF identity-map edit-audit warning.

## Dev Agent Record

### Agent Model Used

Claude Sonnet 5 (claude-sonnet-5)

### Debug Log References

None — implementation proceeded without needing to consult runtime logs; all issues (missing test props after adding `logEventOpen`/`onLogEventOpenChange` to `DashboardPageProps`) were caught and fixed via `tsc -b`.

### Completion Notes List

- All 5 tasks complete, all 5 ACs satisfied.
- `Event` domain entity added with the resolved-ambiguity single discriminator (`TaggedEntityType`/`TaggedEntityId`) + by-value `TaggedEntityName` snapshot, per the story's own Dev Notes — not the three-nullable-FK ERD sketch.
- `CreateEvent` resolves the tag via the existing `ITaggingScaffoldRepository` (no new per-entity repository added), rejects a tag against a missing/already-archived Room/PowerPoint/Device with `EventTaggedEntityArchivedException` (409), and validates Description/OccurredAt with `EventValidationException` (400) — reusing `CreateMeterReading`'s exact clock-skew/min-timestamp constants and reasoning.
- Confirmed `CreateEvent` never calls `IStatusRecomputeService` and never touches any of the 9 files the AD-14 `PatternDetectiveDoesNotReferenceSmartPlugOrEventDataTests` guard scans — that architecture test still passes unmodified (verified directly, not just by omission).
- Confirmed the Log Event sheet uses a plain `fetch`, not `attemptSend`/the offline queue — Event creation is deliberately not added to the Meter-Reading-only offline pattern.
- Migration `AddEvent` added to both `EnergyTracker.Infrastructure.Migrations.Postgres` and `.SqlServer` via `scripts/add-migration.sh` (AD-2); both provider migration tests (part of the full Infrastructure.Tests run below) pass, confirming the migration applies cleanly on both engines.
- `EventRepositoryTests.The_TaggedEntityName_snapshot_survives_a_later_rename_of_the_tagged_Room` reproduces the exact EF identity-map hazard flagged in project-context.md (Story 5.1's `EditTariff` bug class): it mutates the tracked `Room` instance's `Name`/`ArchivedAt` *after* the Event was written through the same `DbContext`, then re-reads through both that same context and a fresh one, confirming `TaggedEntityName` still reads the original snapshot either way.
- Frontend topbar gained a second icon-button entry point (`NotebookPen` icon) next to the existing Smart Plug Import button; `logEventOpen` state lifted into `App.tsx` exactly like `logSheetOpen`, including the same UX-DR13 "a newly-raised regression prompt supersedes an open sheet" behavior extended to cover the Log Event sheet too (not explicitly required by the ACs, but consistent with the existing Log Reading sheet's established discipline — flagged for review in case a narrower reading is preferred).
- No mockup exists for this surface (EXPERIENCE.md flags it as spine-only) — built directly against `LogReadingSheet`'s established visual structure and design tokens per the story's own Dev Notes precedent (Story 3.10's "Clean Up History" control). Manual live-browser verification skipped — no OIDC provider configured in this sandboxed environment (same gap noted in Story 4.1's Dev Agent Record); covered instead by the Api.Tests/component-test suites below.
- Full backend suite green: 311 Application.Tests, 186 Api.Tests, 154 Infrastructure.Tests (incl. both Postgres and SqlServer migration tests), 3 Architecture.Tests (incl. the AD-14 guard test) — all passing, `dotnet build` clean. Full frontend suite green: 357/357 Vitest tests, `tsc -b`/`oxlint`/`vite build` all clean.

### File List

**Backend — new:**
- `src/EnergyTracker.Domain/Event.cs`
- `src/EnergyTracker.Infrastructure/Configurations/EventConfiguration.cs`
- `src/EnergyTracker.Infrastructure.Migrations.Postgres/Migrations/20260918104756_AddEvent.cs`
- `src/EnergyTracker.Infrastructure.Migrations.Postgres/Migrations/20260918104756_AddEvent.Designer.cs`
- `src/EnergyTracker.Infrastructure.Migrations.SqlServer/Migrations/20260918104759_AddEvent.cs`
- `src/EnergyTracker.Infrastructure.Migrations.SqlServer/Migrations/20260918104759_AddEvent.Designer.cs`
- `src/EnergyTracker.Application/Ports/IEventRepository.cs`
- `src/EnergyTracker.Application/EventValidationException.cs`
- `src/EnergyTracker.Application/EventTaggedEntityArchivedException.cs`
- `src/EnergyTracker.Application/CreateEvent.cs`
- `src/EnergyTracker.Infrastructure/Adapters/EventRepository.cs`
- `src/EnergyTracker.Api/Endpoints/EventEndpoints.cs`

**Backend — modified:**
- `src/EnergyTracker.Infrastructure/EnergyTrackerDbContext.cs` (Events DbSet + AD-3 query filter)
- `src/EnergyTracker.Infrastructure.Migrations.Postgres/Migrations/EnergyTrackerDbContextModelSnapshot.cs`
- `src/EnergyTracker.Infrastructure.Migrations.SqlServer/Migrations/EnergyTrackerDbContextModelSnapshot.cs`
- `src/EnergyTracker.Api/Program.cs` (IEventRepository/CreateEvent registration, MapEventEndpoints())

**Frontend — new:**
- `web/src/components/event/log-event-sheet.tsx`
- `web/src/components/event/log-event-sheet.test.tsx`

**Frontend — modified:**
- `web/src/components/dashboard/dashboard-page.tsx` (topbar Log Event entry point, LogEventSheet wiring)
- `web/src/components/dashboard/dashboard-page.test.tsx` (new required props on existing test cases)
- `web/src/App.tsx` (`logEventOpen`/`onLogEventOpenChange` state, UX-DR13 supersession)
- `web/src/locales/en-US/translation.json` (`event` i18n namespace)
- `web/src/locales/de-DE/translation.json` (`event` i18n namespace)

**Tests — new:**
- `tests/EnergyTracker.Application.Tests/CreateEventTests.cs`
- `tests/EnergyTracker.Infrastructure.Tests/EventRepositoryTests.cs`
- `tests/EnergyTracker.Api.Tests/EventEndpointsTests.cs`

### Review Findings

Code review run 2026-09-18 (3 parallel layers: Blind Hunter, Edge Case Hunter, Acceptance Auditor). All suites independently re-verified green by the reviewer: `dotnet build` clean, Application/Api/Infrastructure/Architecture all pass, Vitest 357/357. Findings below were each verified against real source or a live probe — not accepted from a review layer on assertion alone.

**Decision needed**

- [x] [Review][Decision] **RESOLVED — live verification performed 2026-09-18, gate cleared.** Live browser/Auth0 verification never performed — Dev Agent Record states manual verification was skipped (no OIDC provider in the sandbox). Per the standing project gate, a live Auth0/Chrome check blocks review→done rather than being deferred. Finding #7 below (confirmation text rendering inside the topbar icon row) is exactly the class of defect only a live render surfaces.
- [x] [Review][Decision] **RESOLVED — split into 404 + 409.** Nonexistent/foreign tag id is reported as 409 "is archived" — verified by live probe: tagging another household's Room returns `409 {"detail":"Room '3ca778f9-…' is archived."}`. Tenant isolation itself holds correctly (AD-3 query filter makes the foreign row invisible; no name leak) — the defect is that the message asserts something false for an id that never existed, and 409 is the wrong class for "does not exist" when the codebase already has a `*NotFoundException` → 404 convention. The spec's Dev Notes explicitly sanctioned 409 for "doesn't exist or is already archived", so changing it contradicts the spec. Options: (a) keep 409, reword message to not claim "archived"; (b) split into a not-found → 404 plus archived → 409. [src/EnergyTracker.Application/CreateEvent.cs:86,96,106]
- [x] [Review][Decision] **RESOLVED — error-code contract added.** Backend error text is surfaced untranslated to de-DE users — `setError(err.detail ?? t('event.errorGeneric'))` shows the hardcoded English exception message, raw GUID included, regardless of locale. A proper fix needs an error-code contract between API and client (AD-18 territory); the cheap fix loses the specific message. [web/src/components/event/log-event-sheet.tsx:209]
- [x] [Review][Decision] **RESOLVED — filter by ancestor state, both layers.** A Power Point / Device whose parent Room is archived is still a valid tag target — `ArchiveRoom` deliberately does not cascade, and both the picker and `CreateEvent` check only the child's own `ArchivedAt`. The picker therefore lists "Kitchen → Counter outlet" under an archived Kitchen and the server accepts it. Whether to filter by ancestor archive state is a product call, not an obvious bug. [src/EnergyTracker.Application/CreateEvent.cs:95,105; web/src/components/event/log-event-sheet.tsx:132,139]
- [ ] [Review][Decision] **RESOLVED — AC #4 split: persistence half CONFIRMED live, display half NOT satisfied (follow-up story required).** The AC is written in display terms ("remains inert display text rather than a broken reference"). Its persistence half is now verified end-to-end in a real browser against a real database (2026-09-18): after the tagged Power Point was archived, the Event row still read snapshot `HiFi` while the live row was archived — the by-value snapshot is genuinely inert, not re-derived. Its display half remains unimplemented: there is no GET/list endpoint (spec-sanctioned, Task 3.1) and nothing renders `taggedEntityName`, so "remains inert **display text**" cannot be demonstrated. This AC closes only when the Event read path ships in the follow-up story.

Live verification (2026-09-18, Chrome against a real Auth0 session, Postgres + API + Vite running locally):
- Authenticated session confirmed (`GET /api/session` → 200) before any Event interaction; the `AddEvent` migration applied cleanly to the live database.
- Topbar entry point renders; the sheet opens with the description field, a prefilled backfillable date/time, and a live tag picker populated from the real scaffold.
- Tagged create succeeded: `POST /api/events` → **200**, persisted as `TaggedEntityType=PowerPoint`, `TaggedEntityName=HiFi` — the AD-10 by-value snapshot, verified directly in Postgres.
- **AC #4 (persistence half) confirmed live:** after archiving the tagged Power Point, the Event row still reads snapshot `HiFi` while the live row is archived, and the archived entry disappeared from the picker (ancestor/archive filtering working in a real render).
- **Confirmation-placement fix confirmed:** "Saved: …" renders in the page body below the header; the topbar icon row is undisturbed. This was the defect the review predicted only a live render would surface.
- **Error-code localization confirmed:** submitting against a Room archived mid-session returned **409** and the UI rendered "That tag has been archived. Please pick another one." — the catalog message, with no English `detail` text and no raw GUID reaching the user.
- **a11y fix confirmed in the live accessibility tree:** the tag picker exposes a `radiogroup` labeled "Tag (optional)" with `radio` children, replacing the previous unlabeled div of plain buttons.
- No console errors during the flow. All test data created for this check was removed and the archived rows restored.
- Not covered: a cold sign-in through the Auth0 login form (an existing valid session was used — entering passwords is outside what I do, so a from-scratch credential login remains yours to run if you want that specific hop exercised).

Resolutions applied (2026-09-18):
- 404/409 split: new `EventTaggedEntityNotFoundException` → 404 for a missing/foreign target; 409 retained for a target that exists but is archived, and reworded so it no longer claims "archived" for something that never existed. This deviates from the spec's stated 409-for-both decision by explicit review decision.
- Error-code contract: every Event exception now carries a stable `ErrorCode` emitted as `errorCode` in the ProblemDetails payload; the SPA maps it to `event.errors.*` catalog entries in both locales and never renders the English `detail` or the raw GUID.
- Ancestor filtering: `CreateEvent` rejects a Power Point under an archived Room and a Device under an archived Power Point or Room (`event.tag_parent_archived`, 409), and the picker no longer offers them.
- AC #4: **persistence half confirmed live** (snapshot stayed `HiFi` after the tagged Power Point was archived — see Live verification above); **display half ruled not satisfied** and deferred to a follow-up story for the Event read path, so story 6.1 cannot close as `done` on this AC.

**Patch**

- [x] [Review][Patch] Null or omitted `description` returns 500 instead of 400 — verified by live probe: both `{"description": null}` and an omitted key return **500**. `description.Trim()` runs before any null check on a non-nullable param bound from JSON; no `RespectNullableAnnotations` or validation filter is configured, so STJ binds null and the NRE escapes both catch blocks. Project precedent is the opposite: `TaggingScaffoldNameValidator.Validate(string? name)` leads with `IsNullOrWhiteSpace`. Violates spec Task 2.4 and the RFC 7807 rule. [src/EnergyTracker.Application/CreateEvent.cs:27]
- [x] [Review][Patch] No test covers AD-3 cross-household tag isolation — the invariant this feature leans on entirely for authorization is asserted only in a comment. `CreateEventTests` substitutes `ITaggingScaffoldRepository` (filter never runs), `EventEndpointsTests` never creates a second household. Isolation does hold today (probe-verified), so this is a coverage gap, not a live vulnerability — but a future refactor to `FindAsync`/`IgnoreQueryFilters` would open a cross-tenant read with a green suite. [tests/EnergyTracker.Api.Tests/EventEndpointsTests.cs]
- [x] [Review][Patch] `EventRepositoryTests` is Postgres-only — no SQL Server pair, while `TariffRepositoryTests`, `MeterReadingRepositoryTests` and three SmartPlugImport test classes all ship one. AD-2 says portability can only be verified against real engines; the SqlServer migration currently has zero runtime coverage. [tests/EnergyTracker.Infrastructure.Tests/EventRepositoryTests.cs:17]
- [x] [Review][Patch] The AC #4 snapshot test cannot fail, and omits the re-parent case spec Task 5.2 names — `Event` has no navigation property to `Room` and `EventConfiguration` deliberately configures no relationship, so no EF mechanism could rewrite `TaggedEntityName`; both assertions are tautologically true despite the comment claiming it reproduces the Story 5.1 identity-map hazard. Spec requires rename/archive/**re-parent**; only rename+archive are covered, and no PowerPoint- or Device-tagged case exists at all. [tests/EnergyTracker.Infrastructure.Tests/EventRepositoryTests.cs:70]
- [x] [Review][Patch] Post-save confirmation renders inside the topbar icon row — `LogEventSheet` wraps sheet + `<p role="status">Saved: {{description}}</p>` in one flex column, and `dashboard-page.tsx` mounts that whole wrapper in the `justify-between` header next to two `size-10` buttons. Up to 500 chars of user text lands in the header and is only cleared on the next sheet open. `LogReadingSheet` uses the same wrapper but sits in the card body where it is harmless. [web/src/components/event/log-event-sheet.tsx:216,290; web/src/components/dashboard/dashboard-page.tsx:126]
- [x] [Review][Patch] Tag picker is not accessible — `<Label>` has no `htmlFor` and is associated with nothing; the button list has no `role="radiogroup"`/`aria-labelledby`; selection is conveyed only visually via `variant`, with no `aria-checked`/`aria-pressed`. A screen-reader user cannot tell which tag is selected or that the buttons form one choice. [web/src/components/event/log-event-sheet.tsx:254-275]
- [x] [Review][Patch] A failed tag-scaffold fetch is indistinguishable from "no tags exist" — `.catch(() => setTagOptions([]))` feeding `{tagOptions.length > 0 && …}` removes the entire tag block, including the "No tag" control, with no error, spinner or retry. The same silent-empty render covers the in-flight window, so a fast typist can submit before options arrive. [web/src/components/event/log-event-sheet.tsx:154,252]
- [x] [Review][Patch] A 401 during submit is indistinguishable from a generic failure — the 401 carries no ProblemDetails body, so `detail` is null and the user sees the generic message with no re-auth path, retrying forever. Inconsistent with the rest of the app: `App.tsx:148` branches on `status === 401` to route to login, and `meter-reading-sync.ts:84` treats 401/403 as a distinct class. [web/src/components/event/log-event-sheet.tsx:198-209]
- [x] [Review][Patch] Spec Task 5.4's "backfill date/time round-trips" is not asserted through a submit — the test only types into the input and asserts the DOM value; neither submit test touches the date field or asserts the posted `occurredAt`. AC #3 has no frontend proof the edited timestamp reaches the API. [web/src/components/event/log-event-sheet.test.tsx:54]
- [x] [Review][Patch] Spec Task 5.3's 409 case covers Room only — no PowerPoint or Device archived-tag case at the API layer. [tests/EnergyTracker.Api.Tests/EventEndpointsTests.cs:91]
- [x] [Review][Patch] No frontend test covers the error path or the tag-fetch failure branch — the stub always returns 200, so `toApiError`/`setError` and the `.catch` branch are never exercised. [web/src/components/event/log-event-sheet.test.tsx]
- [x] [Review][Patch] No client-side `maxLength` on the description input — >500 chars costs a full round-trip to a 400. [web/src/components/event/log-event-sheet.tsx:228]
- [x] [Review][Patch] Dead i18n key `event.trigger` in both catalogs — referenced nowhere in `web/src`; the dashboard uses `event.entryPointLabel`. Key-set parity is otherwise intact (verified programmatically, zero drift). [web/src/locales/en-US/translation.json, web/src/locales/de-DE/translation.json]
- [x] [Review][Patch] `ApiError`/`toApiError` re-declared instead of reused, and no `lib/event-api.ts` — `LogReadingSheet` imports `ApiError` from the shared module, and every other feature's fetch layer lives in `web/src/lib/*-api.ts` with a colocated test; this component inlines four raw fetches and four DTO interfaces. Two structurally identical but nominally distinct `ApiError` types will silently break `instanceof` if they ever meet. [web/src/components/event/log-event-sheet.tsx:49-67]
- [x] [Review][Patch] `Event.Description` is the only mutable property on an otherwise `init`-only entity — no edit path exists; a settable property on an append-only row invites an edit that bypasses AD-11's `AuditCorrection` mechanism. [src/EnergyTracker.Domain/Event.cs:9]

**Deferred**

- [x] [Review][Defer] Postgres rejects a non-zero-offset `occurredAt` while SQL Server accepts it (AD-2 divergence) [src/EnergyTracker.Infrastructure/Adapters/EventRepository.cs:10] — deferred, pre-existing and project-wide
- [x] [Review][Defer] `datetime-local` → `Date` silently rewrites DST-gap and ambiguous local times [web/src/components/event/log-event-sheet.tsx:194] — deferred, pre-existing
- [x] [Review][Defer] A device clock >5 min fast makes the untouched default timestamp fail server validation [web/src/components/event/log-event-sheet.tsx:87] — deferred, pre-existing
- [x] [Review][Defer] No time-ordered index on `Events` (HouseholdId only) [src/EnergyTracker.Infrastructure/Configurations/EventConfiguration.cs:43] — deferred, belongs with the future read path

**Dismissed as noise (2)**

- No idempotency key on Event creation — the spec explicitly reasoned this through and rejected it ("no retry-after-reconnect scenario that needs upsert-by-key"). Working as designed.
- TOCTOU between the archived-target check and the insert — the resulting state (an Event tagged to a just-archived entity) is precisely the inert-display-text state AC #4 declares correct by design. Benign consequence.

## Change Log

- 2026-09-18: Code review (bmad-code-review, 3 parallel layers). 17 patches applied, 4 items deferred, 2 dismissed. Status review -> in-progress. The live Auth0/Chrome verification gate was subsequently cleared (see Live verification): authenticated session, tagged create 200, AC #4 persistence half confirmed against a real DB, confirmation-placement and error-localization fixes both confirmed in a real render, no console errors. Sole remaining blocker: AC #4's display half requires an Event read path (follow-up story). Post-patch verification: `dotnet build` clean; Application 315, Api 193, Architecture 3, Infrastructure all pass; Vitest 362/362; `tsc -b` and `vite build` clean; oxlint shows only pre-existing warnings in untouched `components/ui` files.
- 2026-09-18: Story 6.1 implemented (dev-story). All 5 tasks complete, all 5 ACs satisfied. Full backend suite green (311 Application + 186 Api + 154 Infrastructure + 3 Architecture), full frontend suite green (357/357), `dotnet build`/`tsc -b`/`oxlint`/`vite build` all clean. Status set to review.
