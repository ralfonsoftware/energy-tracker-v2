---
baseline_commit: eae971d61ec2403af929a85e6fbd20cf25c6f159
---

# Story 6.3: Wattage Plausibility Correlation

Status: review

<!-- Note: Validation is optional. Run validate-create-story for quality check before dev-story. -->

## Story

As a Household member,
I want a logged Event to show a rough correlation against any consumption deviation Pattern Detective observed around that time,
so that I get a plausible, honest explanation without a false sense of precision.

## Acceptance Criteria

1. **Given** a logged Event, **when** a correlation is computed, **then** it's shown as a rough/approximate signal (e.g. "roughly matches the bump seen") — never false precision or a verified attribution claim (FR-17, UX-DR17).
2. **Given** an Event expected to raise consumption (e.g. "gaming session 3h"), **when** correlated, **then** it's checked against a bump; an Event expected to lower consumption (e.g. "away 2 weeks") is checked against a dip — direction is inferred from the Event, never assumed to always be a bump (FR-17).
3. **Given** an Event with no corresponding observable deviation, **when** displayed, **then** it's shown without a correlation — never flagged as wrong (FR-17, UX-DR14).
4. **Given** two Events logged in the same window as one observed deviation, **when** correlated, **then** both receive the correlation — the mapping is many-to-one, not first-match-wins (FR-17).
5. **Given** the AI backend, **when** configured, **then** the choice between a local model (e.g. LMStudio) and a cloud/external API is a Household-level setting, always visible and under the household's control (FR-17, AD-8).
6. **Given** the AI backend is unset or disabled, **when** an Event is logged, **then** the rest of the product functions fully — the correlation is simply absent, and nothing else in the product branches on whether AI is enabled (FR-17, AD-8, NFR14).
7. **Given** the correlation is computed, **when** shown, **then** it's rendered inline with the Event, not as a separate step the household has to trigger (FR-17).

## Tasks / Subtasks

- [x] **Task 1 — AI plausibility port & adapters (AC: #5, #6)**
  - [x] Define `IAiPlausibilityClient` in `src/EnergyTracker.Application/Ports/IAiPlausibilityClient.cs`: one method, `Task<AiPlausibilityDirection?> ClassifyAsync(string eventDescription, IReadOnlyCollection<AiPlausibilityDirection> observedDirections, CancellationToken cancellationToken)`. Define `AiPlausibilityDirection { Bump, Dip }` alongside it (Domain or Application — follow existing enum placement convention, e.g. next to `Status`).
  - [x] Implement `NoOpAiPlausibilityClient` (Infrastructure/Adapters) — always returns `null`. This is what's registered when unconfigured.
  - [x] Implement `OpenAiCompatibleClient` (Infrastructure/Adapters) — plain `HttpClient` call to `{BaseUrl}/v1/chat/completions` (OpenAI-compatible chat/completions shape — see Dev Notes §Latest Technical Info). System prompt instructs classification-only output; parse defensively — any unexpected/malformed response, timeout, or non-2xx **returns `null`, never throws**. This is the concrete mechanism behind NFR14 for the "AI is configured but flaky" case, not just the "unset" case.
  - [x] Wire DI in `Program.cs`: exactly one config value read once at the composition root selects the adapter (`AiPlausibility:BaseUrl` empty/unset → `NoOpAiPlausibilityClient`; else → `OpenAiCompatibleClient` with `BaseUrl` + optional `AiPlausibility:ApiKey` bearer header) — mirrors the existing DB-provider/job-queue selection pattern (consistency-conventions.md). No new NuGet package — `Directory.Packages.props` has zero AI/LLM SDK references today; use `HttpClient`/`System.Text.Json` directly.
  - [x] Tests: an Infrastructure test for `OpenAiCompatibleClient`'s request shaping and defensive response parsing (stub `HttpMessageHandler`, cover malformed/timeout/non-2xx → `null`).

- [x] **Task 2 — Household-level AI setting (AC: #5)**
  - [x] Add `AiPlausibilityEnabled` (`bool`, default `false`) to `Household` (`src/EnergyTracker.Domain/Household.cs`), following the existing `TrendingThresholdKwh`/`LowConfidenceGapDays` config-column pattern (AD-15). Add via `scripts/add-migration.sh` (both providers, AD-2).
  - [x] New use case mirroring `SetYearlyBaseline.cs`'s shape, e.g. `SetAiPlausibilityEnabled.cs` (Application) — updates the flag, respects AD-4 optimistic concurrency (`Household.Version`).
  - [x] New read endpoint exposing what's "always visible" per AC #5: `{ enabled: bool, backendConfigured: bool, backendLabel: string | null }`. `backendConfigured` reflects whether `AiPlausibility:BaseUrl` is set at this deployment; `backendLabel` is a plain deploy-time env var (e.g. `AiPlausibility:BackendLabel = "Local (LMStudio)"` or `"OpenAI"`) — a human-set label, not a heuristic guess from the URL. Extend `HouseholdEndpoints.cs` (or sibling) rather than inventing a new endpoints file, matching the one-file-per-entity convention.
  - [x] Frontend: Settings page gets an on/off toggle bound to `AiPlausibilityEnabled` plus the read-only backend label — always visible per AC #5, not hidden behind an "advanced" section.
  - [x] i18n: add `settings.aiPlausibility.*` keys to both `en-US` and `de-DE` catalogs in the same commit (AD-18).
  - [x] Tests: Application (`SetAiPlausibilityEnabledTests`), Infrastructure dual-provider (Household column round-trip), Api (endpoint), frontend component test for the toggle.

- [x] **Task 3 — Windowed consumption-deviation detection (AC: #2, #3, #4)**
  - [x] **Do not modify** the 9 files the `PatternDetectiveDoesNotReferenceSmartPlugOrEventDataTests` architecture guard source-scans for the literal word "Event" (see Dev Notes §AD-14 guard for the exact list). New logic must live in a **new** file that *reads* Pattern Detective's existing calculations/data (`PatternDetectiveCalculator`, `MeterReading`, `SmartPlugReading`, `Household` baseline config) — the isolation is one-directional; Pattern Detective must stay Event-unaware, but new Event-aware code may consume its outputs.
  - [x] New calculator (e.g. `src/EnergyTracker.Domain/Calculations/WindowedDeviationCalculator.cs`) that, given a Household's `MeterReading`/`SmartPlugReading` data and a `±7 day` window around a timestamp, computes whether the windowed pace deviates meaningfully from the baseline-implied expected rate for that window, returning `Bump`, `Dip`, or `null` (no deviation). **Reuse `BonusDecayNormalizer`'s pace/savings math (AD-5) — do not write a second copy of that formula.** Use `Household.TrendingThresholdKwh` (already exists, already household-tunable) prorated to the window length as the deviation bar, since no other threshold is specified anywhere in the PRD/architecture — flagged as a product-tuning decision, not a value to hardcode-and-forget.
  - [x] Tests: Domain unit tests covering bump / dip / none / boundary-exactly-at-threshold cases.
  - [x] Explicitly re-run `PatternDetectiveDoesNotReferenceSmartPlugOrEventDataTests` after this task and confirm it still passes unmodified — do not assume; verify.

- [x] **Task 4 — Correlate an Event end-to-end (AC: #1, #2, #3, #4, #6, #7)**
  - [x] Add two nullable columns directly to `Event` (`src/EnergyTracker.Domain/Event.cs`): `CorrelationDirection` (`string?`, "Bump"|"Dip") and `CorrelationComputedAtUtc` (`DateTimeOffset?`). Both null = "no correlation" (AC #3) — this is the simplest representation and needs no new table/repository, since correlation is 1:1-owned by its Event with no independent lifecycle. Migration via `scripts/add-migration.sh` (both providers).
  - [x] Add `Task SetCorrelationAsync(Guid eventId, string? direction, DateTimeOffset computedAtUtc, CancellationToken cancellationToken)` to `IEventRepository` + `EventRepository` adapter.
  - [x] New `CorrelateEvent` use case (Application) — payload carries `EventId`, `HouseholdId`, `OccurredAt`, `Description`. Logic: load `Household`; **if `AiPlausibilityEnabled` is false or `IAiPlausibilityClient` resolves to the no-op (unconfigured), do nothing and return** — this is the *only* place in the whole feature that checks "is AI enabled," per AD-8's explicit anti-hard-branch rule (`project-context.md`: "features must not check 'is AI enabled' and take a different code path"). Otherwise: run Task 3's calculator for the Event's window to get observed direction(s); if none, leave correlation null (AC #3); if a deviation exists, call `IAiPlausibilityClient.ClassifyAsync(description, observedDirections, ct)` to see whether the Event's inferred expected direction matches; persist the result via `SetCorrelationAsync`. Must never throw out of `ExecuteAsync` — a job-queue retry storm on an AI/network hiccup would itself become a product-availability issue that NFR14 exists to prevent.
  - [x] Wire `CreateEvent` (`src/EnergyTracker.Application/CreateEvent.cs`) to **unconditionally** enqueue a `JobEnvelope<CorrelateEventPayload>` via `IBackgroundJobQueue` right after `AddAsync` succeeds — no enable/disable check here (that lives entirely in `CorrelateEvent`, per the rule above). This is new wiring: `CreateEvent` currently enqueues nothing at all.
  - [x] Register the job-queue consumer for `CorrelateEventPayload` (mirror how the existing Smart Plug import job consumer is wired in `Program.cs`/Infrastructure).
  - [x] Tests: `CorrelateEventTests` (Application, NSubstitute for `IEventRepository`/`IAiPlausibilityClient`/`IHouseholdRepository`) covering enabled+deviation+match, enabled+deviation+no-match, enabled+no-deviation, disabled, unconfigured, AI-client-throws-nothing-propagates. Infrastructure dual-provider test for the new `Event` columns + `SetCorrelationAsync`. Api test confirming `GetEventHistory`/`GET /api/events` now returns the correlation fields.

- [x] **Task 5 — Frontend inline display (AC: #1, #3, #7)**
  - [x] Extend `EventDto` (`web/src/lib/event-api.ts`) with `correlationDirection: 'Bump' | 'Dip' | null`.
  - [x] In `EventRow`/`events-card.tsx`, render the correlation **inline in the same row** — never a separate step/view (AC #7). No correlation → render nothing extra, no placeholder, no "no match found" state (AC #3, UX-DR14).
  - [x] Display text is **always one of exactly two fixed, translated strings** — never raw AI output rendered verbatim (this is required by AD-18 i18n and UX-DR17's plain-language/no-false-precision voice-and-tone rule, even though it's not spelled out as its own AC): `trendHistory.eventsCard.correlation.bump` → *"Roughly matches the bump seen."* and `.dip` → *"Roughly matches the dip seen."* (exact copy from EXPERIENCE.md's Voice and Tone Do/Don't table — do not paraphrase). Add both keys to `en-US` and `de-DE` in the same commit.
  - [x] Component tests: present (bump), present (dip), absent — confirm absent renders cleanly with no extra markup.

- [x] **Task 6 — Cross-cutting verification (AC: all)**
  - [x] Full backend suite green per-project (Domain/Application/Infrastructure/Api/Architecture — MTP runner rejects a combined multi-project run), full frontend suite green, `tsc -b`, `oxlint`, `vite build` all clean.
  - [x] Live verification gate (project convention — blocks review→done, does not defer): **CLEARED — see Completion Notes' "Live verification" entry.** Confirmed live in Chrome against the real Auth0 test-user session (Postgres + API + Vite running locally): the AI-disabled path (toggle off, no correlation, clean console) and the toggle-on-but-unconfigured-backend path (AC #6) both verified end-to-end. No AI backend is configured in this environment, so the real classification round-trip (AC #1/#7's rendered bump/dip text) was not exercised — explicitly disclosed, not claimed.
  - [x] File anything punted into `_bmad-artifacts/implementation/deferred-work.md` under a `## Deferred from: code review of story-6.3 (date)` heading, with `summary`/`evidence`/`[file:line]`, matching the existing format.

## Dev Notes

### Design decisions made here (flagged for review — mirrors how Story 6.1 resolved its own tagging-shape ambiguity)

Nothing in the PRD/architecture/UX docs fully resolves these; they're genuine open questions this story has to answer to be implementable. Resolutions below are reasoned defaults, not settled team decisions — flag a mismatch for review rather than silently overriding at implementation time.

1. **AC #5's "choice between local and cloud is a Household-level setting" vs. AD-8's "one config value read once at the composition root."** These read as in tension: AD-8/`consistency-conventions.md` describe a single deployment-wide adapter selection (like the DB provider), while AC #5 reads like each Household can independently pick local-vs-cloud. Resolved here as: **one deployment-wide backend** (env-var `BaseUrl`/`ApiKey`, satisfying AD-8 and AD-19's secrets rule — there's no existing precedent anywhere in this codebase for storing a user-supplied secret in the DB), plus a **per-Household on/off toggle** (`AiPlausibilityEnabled`) and an always-visible read-only label of which backend is active. This satisfies "always visible and under the household's control" as *whether this household's Event data is ever sent to AI at all* (consistent with the PRD's privacy stance: "Energy consumption data is treated as sensitive... no telemetry/analytics phone-home by default"), without requiring per-household backend infrastructure that nothing else in the codebase supports today.
2. **Correlation display text is always one of two fixed catalog strings, never raw AI output.** The AI's role is classification only (`AiPlausibilityDirection?`), not prose generation. Required by AD-18 (i18n — a raw AI sentence can't be localized) and by UX-DR17's explicit "no confidence percentage, no claimed causation" plain-language discipline.
3. **Correlation window (±7 days) and deviation threshold (`Household.TrendingThresholdKwh` prorated).** No value is specified anywhere in the source docs. `TrendingThresholdKwh` is reused because it's the only existing household-tunable "how much deviation counts" knob (AD-15) — inventing a second, AI-specific threshold column was rejected as unnecessary duplication.
4. **Correlation stored as two nullable columns on `Event`, not a separate table.** Simplest fit for 1:1, no-independent-lifecycle data; avoids a new repository/port pair for something this small.
5. **`CreateEvent` always enqueues a correlation job; the enable/configured check lives solely inside `CorrelateEvent`.** This is not a style preference — it's required by AD-8's explicit anti-pattern ("features must not check 'is AI enabled' and take a different code path").

### What already exists vs. what's greenfield (from exhaustive codebase research — do not re-derive this, and do not re-implement anything listed as existing)

**Exists already (reuse, don't rebuild):**
- `Event` entity (`src/EnergyTracker.Domain/Event.cs`): `Id`, `HouseholdId`, `Description` (≤500 chars, only mutable-by-convention field), `OccurredAt` (`DateTimeOffset`, backfillable — **not** `EventText`/`Timestamp`, exact field names matter), `CreatedAtUtc`, `TaggedEntityType`/`TaggedEntityId`/`TaggedEntityName` (AD-10 by-value snapshot, no navigation property, deliberately unjoinable).
- `CreateEvent` (Application), `IEventRepository`/`EventRepository` (`AddAsync`, `GetPageForHouseholdAsync(page, pageSize, ct)` — no `householdId` param, AD-3 query filter handles scoping), `EventConfiguration` (EF), `EventEndpoints.cs` (`POST /api/events`, `GET /api/events`).
- `IBackgroundJobQueue`/`JobEnvelope<TPayload>` (Application/Ports) with `InProcessChannelJobQueue`/`AzureStorageQueueJobQueue` adapters — reusable as-is for the new `CorrelateEventPayload`. Status polling is `GET /api/jobs/{id}`, never WebSocket/SSE (AD-6).
- `Household` config-row pattern (`Locale`, `Currency`, `YearlyBaselineKwh`, `TrendingThresholdKwh`, `LowConfidenceGapDays`, `TariffCheckCadenceMonths`, `Version` for AD-4 concurrency) — the template for the new `AiPlausibilityEnabled` column, and the source of the deviation threshold to reuse.
- `BonusDecayNormalizer` (AD-5, `src/EnergyTracker.Domain/Calculations/`) — the *only* place pace/savings math is allowed to live; the new windowed calculator must call into this, not re-derive it.
- AD-19 secrets handling (env vars / Container Apps secrets / `.env`) — directly reusable for the AI API key.
- `EventsCard`/`EventRow` (`web/src/components/event/events-card.tsx`) — the exact insertion point for AC #7's inline rendering.

**Does not exist yet — this story builds it (confirmed by direct codebase search, not assumed from architecture prose):**
- `IAiPlausibilityClient` port and both adapters (`OpenAiCompatibleClient`, no-op). AD-8 describes these in architecture docs, but zero implementation exists in `src/`.
- Any per-window/localized "was there a bump or dip around timestamp T" computation. `PatternDetectiveCalculator` only produces a single whole-history rolling pace scalar (`ComputePaceToDate`) feeding a 3-state `Status` (`WithinRange`/`BelowBaseline`/`Trending`) — nothing windowed.
- Any Household-level AI-backend setting/column/endpoint.
- Any correlation field on `Event`/`EventDto`, and no background job is triggered by `CreateEvent` today (it's a single synchronous `AddAsync`).
- Any AI/LLM package reference in `Directory.Packages.props` (confirmed zero matches for LMStudio/OpenAI/Semantic Kernel/Ollama) — don't add one; use `HttpClient` directly, consistent with the stack's boring-tools default.

### AD-14 guard — the file list you must not touch

`tests/EnergyTracker.Architecture.Tests/PatternDetectiveDoesNotReferenceSmartPlugOrEventDataTests.cs` source-scans these 9 files and fails the build if the literal word `Event` appears in any of them (written during Story 6.1 in anticipation of exactly this story):

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

The isolation is one-directional: new Event-aware code (Task 3/4) may freely *call* `PatternDetectiveCalculator`/`BonusDecayNormalizer` and *read* `MeterReading`/`SmartPlugReading`/`Household` data — it just must not add the word "Event" into these 9 files themselves. Re-run this test explicitly after Task 3; don't assume it still passes.

### Latest technical info — OpenAI-compatible chat/completions shape (for `OpenAiCompatibleClient`)

LMStudio's local server and effectively every cloud LLM provider (OpenAI, and OpenAI-compatible proxies for others) expose the same request/response shape at `POST {BaseUrl}/v1/chat/completions`:
```json
// request
{ "model": "<provider-specific-or-ignored-by-lmstudio>", "messages": [{"role": "system", "content": "..."}, {"role": "user", "content": "..."}], "temperature": 0 }
// response
{ "choices": [ { "message": { "role": "assistant", "content": "<text>" } } ] }
```
Auth is a `Authorization: Bearer {ApiKey}` header when `ApiKey` is configured (LMStudio typically needs none). Use `temperature: 0` for a classification task — this isn't a creative-writing call. Parse `choices[0].message.content` defensively (trim, case-insensitive match against "Bump"/"Dip"/"None"); anything else → `null`. No SDK needed; a small typed request/response DTO pair over `HttpClient`/`System.Text.Json` is enough and matches the project's existing preference for framework-native tools over added dependencies.

### Previous story (6.2) intelligence

- Events render as a card (`events-card.tsx`) on the **Trend History page**, between Meter Readings and Per-Plug cards — not inside the Log Event sheet. 6.2's own Dev Notes anticipated this story explicitly: *"Story 6.3 renders its correlation 'inline with the Event', and a persistent list row is a better host for that than a sheet the household must reopen."* Confirms Task 5's insertion point.
- AD-10 discipline (snapshot once, render forever, never rejoin) is already fully implemented in the display path — follow the same discipline for correlation (it's derived once by the background job and just read back, never recomputed at render time).
- Live Auth0/Chrome verification is a standing project gate that blocks review→done and does **not** defer, even when component tests exist — 6.2's own review reopened a "closed" task specifically because a live check had been skipped. Expect equally strict scrutiny here for the AI-enabled/disabled toggle paths (this is also recorded in project memory: hotfix/Auth0 live-check gates don't defer).
- `.NET` testing conventions to repeat: `{SubjectClass}Tests` naming, `Snake_case_with_underscores` test method names, Shouldly assertions, NSubstitute against `Application/Ports`, `TestContext.Current.CancellationToken`, dual-provider Infrastructure tests added to an abstract `*RepositoryTestsBase` (not per-provider subclasses).
- i18n: every new user-facing string and every new error code needs both `en-US` and `de-DE` catalog entries in the same commit, verified for zero key-drift; error codes are a ProblemDetails `errorCode` extension mapped client-side, never raw server `detail` text rendered.
- Definition of done used on 6.1/6.2: `dotnet build`; `dotnet test` per project separately (MTP runner rejects a combined multi-project run); `npx vitest run`; `npx tsc -b`; `npx oxlint`; `npx vite build`.
- Review process: 3 parallel adversarial layers (Blind Hunter, Edge Case Hunter, Acceptance Auditor), findings triaged Decision/Patch/Defer/Dismissed, patches re-verified against the full suite before `done`.
- No deferred-work.md entries reference AI/LMStudio/correlation yet — this area is untouched by prior deferrals; nothing to reconcile there.

### Git/file convention intelligence (reuse these exact paths/patterns)

- Domain: `src/EnergyTracker.Domain/Event.cs`, `Household.cs`, `Calculations/BonusDecayNormalizer.cs`, `Calculations/PatternDetectiveCalculator.cs`.
- Application: one flat use-case-per-file, no feature folders (AD-1) — e.g. `CreateEvent.cs`, `GetEventHistory.cs`, `SetYearlyBaseline.cs` as the shape template for `SetAiPlausibilityEnabled.cs`/`CorrelateEvent.cs`. Ports live in `Application/Ports/I{Capability}.cs`.
- Infrastructure adapters in `Infrastructure/Adapters/{Vendor}{Capability}.cs`; EF configs in `Infrastructure/Configurations/{Entity}Configuration.cs`; migrations always in **pairs** across `EnergyTracker.Infrastructure.Migrations.Postgres` and `...Migrations.SqlServer`, added only via `scripts/add-migration.sh <Name>` (never `dotnet ef migrations add` directly — AD-2).
- Api: one endpoints file per entity/capability (`EventEndpoints.cs`, `HouseholdEndpoints.cs`), DI/mapping registered in `Program.cs`.
- Frontend: one feature folder per domain concept (`web/src/components/event/`), fetch layer in its own `web/src/lib/{feature}-api.ts` with a colocated test and a local `ApiError`/`toApiError`, i18n keys namespaced per surface (`event.*` for the write-side sheet, `trendHistory.eventsCard.*` for the read-side card — keep the new `correlation.*`/`aiPlausibility.*` keys under the matching existing namespace, don't invent a third).
- Tests mirror `src/` 1:1 into `tests/{Layer}.Tests`; `EnergyTracker.Architecture.Tests` holds spine-invariant guard tests (the AD-14 one above) — if this story establishes a new invariant worth guarding, consider adding one here too, per project-context.md's own guidance.
- Commit convention: `feat: <description> (story 6.3, FR-17)`, ending with the attribution line this session's own system instructions specify.

### Project Structure Notes

- No conflicts detected with the unified project structure — every new file this story adds follows an existing, already-established pattern (see conventions above). The one structural addition with no direct precedent is the `CorrelateEvent` background-job consumer wiring; follow the existing Smart Plug import job consumer's registration shape in `Program.cs` as the closest analog.
- No mockup exists for this surface (confirmed: `EXPERIENCE.md`'s composition-reference list explicitly marks Log Event as "spine-only, no rendered mock," and the `mockups/` directory has no Event/correlation-named file). This mirrors Stories 6.1/6.2 and 3.10's precedent of building directly against established tokens/components rather than waiting on a dedicated UX pass — flag any visual mismatch for a later UX pass rather than blocking on one now.
- Reusable visual precedent for the inline correlation indicator: the Meter Readings list's plain-outline "Pending" badge (`components.md`) is the nearest analog for a neutral, non-status-color inline indicator — the 3-state Status color triad (`colors.md`) must **never** be reused for this, it's reserved exclusively for Pattern Detective Status. Given UX-DR17 wants plain-language text over a badge/icon anyway ("Roughly matches the bump seen." as prose, not a chip), a plain-outline `Badge` or even unstyled inline text next to the row is more faithful to the prescribed copy than inventing a new colored indicator — leave a colored-badge approach to a follow-up if a real UX pass wants one.

### Testing Standards Summary

- Backend: Shouldly + NSubstitute, `Snake_case_with_underscores` test names, `{SubjectClass}Tests` per file, dual-provider Testcontainers tests for anything touching the DB, `TestContext.Current.CancellationToken` in async tests.
- Frontend: colocated Vitest + Testing Library, jsdom, globals on.
- New Architecture-layer concern: after Task 3, explicitly re-verify `PatternDetectiveDoesNotReferenceSmartPlugOrEventDataTests` passes unmodified — this is the single test most likely to catch an accidental AD-14 violation in this story.
- Live verification gate (project-standard, does not defer): both the AI-disabled path and, if a backend is reachable in-environment, the AI-enabled path must be confirmed in a real render before this story can move past review.

### References

- [Source: _bmad-artifacts/planning/epics/epic-6-context-capture-wattage-plausibility.md#Story 6.3] — Story statement and all 7 ACs (verbatim source).
- [Source: _bmad-artifacts/planning/architecture/architecture-energy-tracker-2026-08-09/ARCHITECTURE-SPINE/invariants-rules.md#AD-8] — `IAiPlausibilityClient`/`OpenAiCompatibleClient` port-and-adapter mandate, no-op-when-unconfigured rule.
- [Source: _bmad-artifacts/planning/architecture/architecture-energy-tracker-2026-08-09/ARCHITECTURE-SPINE/invariants-rules.md#AD-10] — soft-delete/by-value snapshot discipline already implemented on `Event`.
- [Source: _bmad-artifacts/planning/architecture/architecture-energy-tracker-2026-08-09/ARCHITECTURE-SPINE/consistency-conventions.md] — config-driven single-adapter-selection pattern.
- [Source: _bmad-artifacts/planning/architecture/architecture-energy-tracker-2026-08-09/ARCHITECTURE-SPINE/deferred.md] — anticipates AI-correlation load as a future driver for splitting the worker process.
- [Source: tests/EnergyTracker.Architecture.Tests/PatternDetectiveDoesNotReferenceSmartPlugOrEventDataTests.cs] — the AD-14 guard's exact 9-file list.
- [Source: src/EnergyTracker.Domain/Event.cs, CreateEvent.cs, Ports/IEventRepository.cs, Infrastructure/Adapters/EventRepository.cs, Api/Endpoints/EventEndpoints.cs] — existing Event vertical slice to extend.
- [Source: src/EnergyTracker.Domain/Calculations/PatternDetectiveCalculator.cs, BonusDecayNormalizer.cs, GetCurrentStatus.cs] — existing Pattern Detective computation to read from, never modify for Event-awareness.
- [Source: src/EnergyTracker.Domain/Household.cs] — config-row pattern for the new `AiPlausibilityEnabled` column and the reused `TrendingThresholdKwh`.
- [Source: src/EnergyTracker.Application/Ports/IBackgroundJobQueue.cs] — `JobEnvelope<TPayload>` pattern for the new `CorrelateEventPayload`.
- [Source: _bmad-artifacts/planning/prds/prd-energy-tracker-2026-08-08/prd/4-features.md#FR-16, #FR-17] — full feature text and testable consequences.
- [Source: _bmad-artifacts/planning/prds/prd-energy-tracker-2026-08-08/prd/cross-cutting-nfrs.md, constraints-and-guardrails.md] — NFR14 graceful-degradation wording, privacy stance on Event data leaving the deployment.
- [Source: _bmad-artifacts/planning/prds/prd-energy-tracker-2026-08-08/prd/3-glossary.md#Wattage Plausibility, #Pattern Detective, #Event] — canonical term definitions.
- [Source: _bmad-artifacts/planning/ux-designs/ux-energy-tracker-2026-08-08/EXPERIENCE.md] — Voice and Tone Do/Don't table (exact prescribed correlation copy), State Patterns table (no-deviation treatment, UX-DR14), Component Patterns table's "Wattage Plausibility correlation display" row (UX-DR17), Information Architecture table (Log Event surface, UX-DR12).
- [Source: _bmad-artifacts/planning/epics/requirements-inventory.md#UX-DR12, #UX-DR14, #UX-DR17] — canonical UX-DR definitions.
- [Source: _bmad-artifacts/implementation/6-2-event-history-view.md] — previous story's Dev Notes/Dev Agent Record (Events-card location, AD-10 display discipline, live-verification gate precedent).
- [Source: _bmad-artifacts/implementation/6-1-event-logging.md] — Event entity design decisions, no-mockup precedent and its resolution path.
- [Source: _bmad-artifacts/implementation/deferred-work.md] — confirmed no prior deferrals touch this feature area yet.

## Dev Agent Record

### Agent Model Used

Claude Sonnet 5 (claude-sonnet-5)

### Debug Log References

- `./scripts/add-migration.sh AddAiPlausibilityEnabledToHousehold` — succeeded both providers.
- `./scripts/add-migration.sh AddCorrelationToEvent` — succeeded both providers.
- `dotnet build EnergyTracker.sln` — 0 warnings, 0 errors (final pass).
- `dotnet test tests/EnergyTracker.Application.Tests/...` — 344/344 passed.
- `dotnet test tests/EnergyTracker.Infrastructure.Tests/...` (dual-provider Testcontainers) — 214/214 passed.
- `dotnet test tests/EnergyTracker.Api.Tests/...` — 206/206 passed.
- `dotnet test tests/EnergyTracker.Architecture.Tests/...` — 4/4 passed, including `PatternDetectiveDoesNotReferenceSmartPlugOrEventDataTests` re-verified unmodified after Task 3.
- `npx vitest run` (web/) — 376/376 passed.
- `npx tsc -b` (web/) — clean.
- `npx oxlint` (web/) — clean (pre-existing warnings only, in files this story didn't touch).
- `npx vite build` (web/) — clean.
- `npx shadcn@latest add switch` — generated `web/src/components/ui/switch.tsx` with a wrong `cn` import (`from "cn"` instead of the project's `@/lib/utils` alias) and added a spurious `cn` npm dependency; both corrected by hand (`npm uninstall cn`, fixed the import).
- First live verification attempt (initial dev-story pass): `mcp__claude-in-chrome__tabs_context_mcp` → "Browser extension is not connected." Local stack (Postgres via docker-compose, API via `scripts/run-api.sh`, Vite dev server) was started and confirmed healthy (`GET /health` → 200) in preparation, then stopped after the extension proved unavailable.
- Second attempt, same session, user-requested retry: extension connected successfully (`tabs_context_mcp` returned a live tab). Local stack restarted (Postgres container was already up; API + Vite dev server restarted) — see "Live verification" under Completion Notes for the full run.

### Completion Notes List

- Implemented all 6 tasks. AC #1–#7 satisfied by automated tests, and the AI-disabled / toggle-on-unconfigured-backend paths additionally confirmed live in Chrome (see "Live verification" below). The real AI classification round-trip (a configured, reachable backend) was not exercised — no `AiPlausibility:BaseUrl` is configured in this environment.

**Live verification (2026-09-21, Chrome against the real Auth0 test-user session already signed in from prior stories' verifications, Postgres + API + Vite running locally via `scripts/migrate.sh`/`scripts/run-api.sh`/`npm run dev`):**
- Settings page: "AI Wattage Plausibility" card renders unconditionally (AC #5) with the toggle off and "No AI backend is configured for this deployment." — matches this environment's actual config (no `AiPlausibility:BaseUrl` set).
- `GET /api/households/{id}/ai-plausibility` → 200 (confirmed via `read_network_requests`), rendering the fetched state correctly.
- Toggled the switch on: `PUT /api/households/{id}/ai-plausibility` → 200, switch updated to checked, no console errors.
- Logged a real Event ("Story 6.3 live verification: gaming session 3h") via the Log Event sheet with the toggle now on: `POST /api/events` → 200, confirmation banner rendered. Server log confirmed `CreateEvent` enqueued a `CorrelateEvent` background job, which ran, queried `Households` (including the new `AiPlausibilityEnabled` column), found no `YearlyBaselineKwh` set, and completed cleanly with `Status = Completed` and no exception — the exact AC #6 "AI enabled but nothing to correlate against yet" degrade-gracefully path.
- Trend History → Events card: the new Event renders inline in its row with no correlation text (AC #3 — correctly absent, not flagged as wrong, no placeholder) and no separate view/step was needed (AC #7).
- `read_console_messages` (`onlyErrors: true`) was clean throughout every step above.
- Toggle switched back off afterward (`PUT` → 200) to leave local-dev state as found; the test Event itself was left in place (Events are append-only — no delete path exists), matching Story 6.2's own "expected, inert local-dev state" precedent.
- **Not verified live:** a real AI classification round-trip and its rendered "Roughly matches the bump/dip seen." text (AC #1/#7's actual copy) — this environment has no reachable `AiPlausibility:BaseUrl` configured (LMStudio/OpenAI/etc.), so `IAiPlausibilityClient` resolves to `NoOpAiPlausibilityClient` regardless of the toggle. Covered instead by `OpenAiCompatibleClientTests` (stubbed HTTP) and `events-card.test.tsx`'s component tests for the bump/dip rendering.
- **Design decisions resolved during implementation (flagged for review, per this story's own Dev Notes framing):**
  1. `WindowedDeviationCalculator` (Task 3) reads only `MeterReading` data, never `SmartPlugReading` — AD-14 explicitly forbids summing `SmartPlugReading` into a figure compared against the Main Meter total, so the windowed pace/delta is computed exactly like `PatternDetectiveCalculator` does (Main-Meter-only), just over a fixed ±7-day window instead of a trailing-365-day one anchored on the latest reading.
  2. The ±7-day window's deviation threshold is `Household.TrendingThresholdKwh` **re-run through `BonusDecayNormalizer.NormalizeToDate` a second time** (same call used for the expected-consumption figure, just with `TrendingThresholdKwh` as the "annual rate" argument instead of `YearlyBaselineKwh`) — this reuses AD-5's one proration formula for both figures rather than inventing a second, window-specific proration, and keeps the two figures on an identical decimal-precision basis.
  3. `CorrelateEvent` does **not** apply AD-12's open-`MeterRegressionPrompt` exclusion `GetCurrentStatus` applies via `PatternDetectiveCalculator.ExcludeFromOpenPrompt` — a deliberate scope simplification given AC #1's own "rough/approximate, never false precision" framing. Filed to `deferred-work.md`.
  4. Correlation is derived exactly once, right after Event creation, and never recomputed later even if a Meter Reading is subsequently backfilled into an already-correlated Event's window (AD-10's "derive once, read back forever" discipline, applied by extension). Filed to `deferred-work.md`.
  5. `GetInWindowByMainMeterAsync` (new `IMeterReadingRepository` method) does a plain `[windowStart, windowEnd]` inclusive filter with no bracketing/interpolation from outside the window — matches Task 3's literal "±7 day window around a timestamp" wording; if zero or one reading falls inside that exact window, the correlation is simply absent (AC #3), even if readings exist just outside it.
- The `npx shadcn@latest add switch` CLI run added a broken `cn` import and an unnecessary `cn` npm package (see Debug Log) — this looks like a bug/version mismatch in the shadcn CLI given this project's `components.json` already declares the standard `@/lib/utils` alias; corrected by hand rather than accepting the generated import as-is.
- **Live verification:** the Claude-in-Chrome extension reported "not connected" on the first attempt; the user asked for a retry later in the same session, at which point it connected successfully. The AI-disabled path and the toggle-on-but-unconfigured-backend path (AC #5, #6) were both confirmed live (see "Live verification" entry above) — gate cleared for those. No `AiPlausibility:BaseUrl` is configured in this environment, so the real AI classification round-trip and its rendered bump/dip text (AC #1/#7's actual copy) still was not, and could not be, exercised live here — that specific gap is filed to `deferred-work.md`.
- Full backend suite green per-project (Application 344, Infrastructure 214 dual-provider, Api 206, Architecture 4 including the re-verified AD-14 guard); full frontend suite green (376 Vitest); `tsc -b`, `oxlint`, `vite build` all clean.

### File List

**New files:**
- `src/EnergyTracker.Domain/AiPlausibilityDirection.cs`
- `src/EnergyTracker.Domain/Calculations/WindowedDeviationCalculator.cs`
- `src/EnergyTracker.Application/Ports/IAiPlausibilityClient.cs`
- `src/EnergyTracker.Application/AiPlausibilityBackendOptions.cs`
- `src/EnergyTracker.Application/SetAiPlausibilityEnabled.cs`
- `src/EnergyTracker.Application/CorrelateEvent.cs`
- `src/EnergyTracker.Infrastructure/Adapters/NoOpAiPlausibilityClient.cs`
- `src/EnergyTracker.Infrastructure/Adapters/OpenAiCompatibleClient.cs`
- `src/EnergyTracker.Infrastructure.Migrations.Postgres/Migrations/20260921050717_AddAiPlausibilityEnabledToHousehold.cs` (+ `.Designer.cs`)
- `src/EnergyTracker.Infrastructure.Migrations.Postgres/Migrations/20260921051234_AddCorrelationToEvent.cs` (+ `.Designer.cs`)
- `src/EnergyTracker.Infrastructure.Migrations.SqlServer/Migrations/20260921050719_AddAiPlausibilityEnabledToHousehold.cs` (+ `.Designer.cs`)
- `src/EnergyTracker.Infrastructure.Migrations.SqlServer/Migrations/20260921051236_AddCorrelationToEvent.cs` (+ `.Designer.cs`)
- `tests/EnergyTracker.Application.Tests/WindowedDeviationCalculatorTests.cs`
- `tests/EnergyTracker.Application.Tests/CorrelateEventTests.cs`
- `tests/EnergyTracker.Infrastructure.Tests/OpenAiCompatibleClientTests.cs`
- `tests/EnergyTracker.Infrastructure.Tests/HouseholdRepositoryTests.cs`
- `tests/EnergyTracker.Api.Tests/AiPlausibilityEndpointsTests.cs`
- `web/src/components/ui/switch.tsx`
- `web/src/components/ai-plausibility/ai-plausibility-form.tsx`
- `web/src/components/ai-plausibility/ai-plausibility-form.test.tsx`

**Modified files:**
- `src/EnergyTracker.Domain/Household.cs`
- `src/EnergyTracker.Domain/Event.cs`
- `src/EnergyTracker.Application/Ports/IHouseholdRepository.cs`
- `src/EnergyTracker.Application/Ports/IEventRepository.cs`
- `src/EnergyTracker.Application/Ports/IMeterReadingRepository.cs`
- `src/EnergyTracker.Application/CreateEvent.cs`
- `src/EnergyTracker.Application/JobTypes.cs`
- `src/EnergyTracker.Infrastructure/Adapters/HouseholdRepository.cs`
- `src/EnergyTracker.Infrastructure/Adapters/EventRepository.cs`
- `src/EnergyTracker.Infrastructure/Adapters/MeterReadingRepository.cs`
- `src/EnergyTracker.Infrastructure/Adapters/BackgroundJobProcessor.cs`
- `src/EnergyTracker.Infrastructure/Configurations/HouseholdConfiguration.cs`
- `src/EnergyTracker.Infrastructure/Configurations/EventConfiguration.cs`
- `src/EnergyTracker.Infrastructure.Migrations.Postgres/Migrations/EnergyTrackerDbContextModelSnapshot.cs`
- `src/EnergyTracker.Infrastructure.Migrations.SqlServer/Migrations/EnergyTrackerDbContextModelSnapshot.cs`
- `src/EnergyTracker.Api/Program.cs`
- `src/EnergyTracker.Api/Endpoints/HouseholdEndpoints.cs`
- `src/EnergyTracker.Api/Endpoints/EventEndpoints.cs`
- `tests/EnergyTracker.Application.Tests/CreateEventTests.cs`
- `tests/EnergyTracker.Infrastructure.Tests/EventRepositoryTests.cs`
- `tests/EnergyTracker.Infrastructure.Tests/MeterReadingRepositoryTests.cs`
- `tests/EnergyTracker.Api.Tests/EventEndpointsTests.cs`
- `web/src/lib/event-api.ts`
- `web/src/components/event/events-card.tsx`
- `web/src/components/event/events-card.test.tsx`
- `web/src/components/settings/settings-page.tsx`
- `web/src/components/settings/settings-page.test.tsx`
- `web/src/App.test.tsx`
- `web/src/locales/en-US/translation.json`
- `web/src/locales/de-DE/translation.json`
- `_bmad-artifacts/implementation/deferred-work.md`

## Change Log

- 2026-09-21: Live Auth0/Chrome verification performed at the user's request (extension reconnected after an initial "not connected" failure). Confirmed live: Settings' AI Wattage Plausibility card (always visible, correct unconfigured-backend messaging), the enable/disable toggle's GET/PUT round trip, and logging a real Event with the toggle on — the background `CorrelateEvent` job ran and completed cleanly with no `YearlyBaselineKwh` set (AC #6's graceful-degrade path), the Event rendered inline in Trend History with no correlation text (AC #3, #7), and the console/network were clean throughout. Toggle switched back off afterward; the test Event left in place (append-only, no delete path — matches Story 6.2's precedent). The real AI classification round-trip (AC #1/#7's bump/dip copy) remains unverified live — no `AiPlausibility:BaseUrl` is configured in this environment — and stays filed in `deferred-work.md`, covered instead by `OpenAiCompatibleClientTests`/`events-card.test.tsx`.
- 2026-09-21: Story implemented (dev-story). All 6 tasks complete, all 7 ACs satisfied by automated tests. Full backend suite green (344 Application + 214 Infrastructure dual-provider + 206 Api + 4 Architecture, including a re-verified AD-14 guard), full frontend suite green (376/376), `dotnet build`/`tsc -b`/`oxlint`/`vite build` all clean. Resolved AD-8's local-vs-cloud tension per the story's own pre-flagged decision #1 (one deployment-wide `OpenAiCompatibleClient` backend + per-Household `AiPlausibilityEnabled` toggle). Windowed deviation math reuses `BonusDecayNormalizer` for both the expected-consumption figure and the prorated threshold — no second proration formula. Live Auth0/Chrome verification could **not** be performed this session (Claude-in-Chrome extension reported "not connected"), even though a real OIDC provider and local Postgres are both configured here — disclosed in Completion Notes and `deferred-work.md` rather than silently skipped, per this project's live-verification-gate convention. Status set to review.
