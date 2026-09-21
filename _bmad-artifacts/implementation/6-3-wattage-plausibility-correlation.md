# Story 6.3: Wattage Plausibility Correlation

Status: ready-for-dev

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

- [ ] **Task 1 — AI plausibility port & adapters (AC: #5, #6)**
  - [ ] Define `IAiPlausibilityClient` in `src/EnergyTracker.Application/Ports/IAiPlausibilityClient.cs`: one method, `Task<AiPlausibilityDirection?> ClassifyAsync(string eventDescription, IReadOnlyCollection<AiPlausibilityDirection> observedDirections, CancellationToken cancellationToken)`. Define `AiPlausibilityDirection { Bump, Dip }` alongside it (Domain or Application — follow existing enum placement convention, e.g. next to `Status`).
  - [ ] Implement `NoOpAiPlausibilityClient` (Infrastructure/Adapters) — always returns `null`. This is what's registered when unconfigured.
  - [ ] Implement `OpenAiCompatibleClient` (Infrastructure/Adapters) — plain `HttpClient` call to `{BaseUrl}/v1/chat/completions` (OpenAI-compatible chat/completions shape — see Dev Notes §Latest Technical Info). System prompt instructs classification-only output; parse defensively — any unexpected/malformed response, timeout, or non-2xx **returns `null`, never throws**. This is the concrete mechanism behind NFR14 for the "AI is configured but flaky" case, not just the "unset" case.
  - [ ] Wire DI in `Program.cs`: exactly one config value read once at the composition root selects the adapter (`AiPlausibility:BaseUrl` empty/unset → `NoOpAiPlausibilityClient`; else → `OpenAiCompatibleClient` with `BaseUrl` + optional `AiPlausibility:ApiKey` bearer header) — mirrors the existing DB-provider/job-queue selection pattern (consistency-conventions.md). No new NuGet package — `Directory.Packages.props` has zero AI/LLM SDK references today; use `HttpClient`/`System.Text.Json` directly.
  - [ ] Tests: an Infrastructure test for `OpenAiCompatibleClient`'s request shaping and defensive response parsing (stub `HttpMessageHandler`, cover malformed/timeout/non-2xx → `null`).

- [ ] **Task 2 — Household-level AI setting (AC: #5)**
  - [ ] Add `AiPlausibilityEnabled` (`bool`, default `false`) to `Household` (`src/EnergyTracker.Domain/Household.cs`), following the existing `TrendingThresholdKwh`/`LowConfidenceGapDays` config-column pattern (AD-15). Add via `scripts/add-migration.sh` (both providers, AD-2).
  - [ ] New use case mirroring `SetYearlyBaseline.cs`'s shape, e.g. `SetAiPlausibilityEnabled.cs` (Application) — updates the flag, respects AD-4 optimistic concurrency (`Household.Version`).
  - [ ] New read endpoint exposing what's "always visible" per AC #5: `{ enabled: bool, backendConfigured: bool, backendLabel: string | null }`. `backendConfigured` reflects whether `AiPlausibility:BaseUrl` is set at this deployment; `backendLabel` is a plain deploy-time env var (e.g. `AiPlausibility:BackendLabel = "Local (LMStudio)"` or `"OpenAI"`) — a human-set label, not a heuristic guess from the URL. Extend `HouseholdEndpoints.cs` (or sibling) rather than inventing a new endpoints file, matching the one-file-per-entity convention.
  - [ ] Frontend: Settings page gets an on/off toggle bound to `AiPlausibilityEnabled` plus the read-only backend label — always visible per AC #5, not hidden behind an "advanced" section.
  - [ ] i18n: add `settings.aiPlausibility.*` keys to both `en-US` and `de-DE` catalogs in the same commit (AD-18).
  - [ ] Tests: Application (`SetAiPlausibilityEnabledTests`), Infrastructure dual-provider (Household column round-trip), Api (endpoint), frontend component test for the toggle.

- [ ] **Task 3 — Windowed consumption-deviation detection (AC: #2, #3, #4)**
  - [ ] **Do not modify** the 9 files the `PatternDetectiveDoesNotReferenceSmartPlugOrEventDataTests` architecture guard source-scans for the literal word "Event" (see Dev Notes §AD-14 guard for the exact list). New logic must live in a **new** file that *reads* Pattern Detective's existing calculations/data (`PatternDetectiveCalculator`, `MeterReading`, `SmartPlugReading`, `Household` baseline config) — the isolation is one-directional; Pattern Detective must stay Event-unaware, but new Event-aware code may consume its outputs.
  - [ ] New calculator (e.g. `src/EnergyTracker.Domain/Calculations/WindowedDeviationCalculator.cs`) that, given a Household's `MeterReading`/`SmartPlugReading` data and a `±7 day` window around a timestamp, computes whether the windowed pace deviates meaningfully from the baseline-implied expected rate for that window, returning `Bump`, `Dip`, or `null` (no deviation). **Reuse `BonusDecayNormalizer`'s pace/savings math (AD-5) — do not write a second copy of that formula.** Use `Household.TrendingThresholdKwh` (already exists, already household-tunable) prorated to the window length as the deviation bar, since no other threshold is specified anywhere in the PRD/architecture — flagged as a product-tuning decision, not a value to hardcode-and-forget.
  - [ ] Tests: Domain unit tests covering bump / dip / none / boundary-exactly-at-threshold cases.
  - [ ] Explicitly re-run `PatternDetectiveDoesNotReferenceSmartPlugOrEventDataTests` after this task and confirm it still passes unmodified — do not assume; verify.

- [ ] **Task 4 — Correlate an Event end-to-end (AC: #1, #2, #3, #4, #6, #7)**
  - [ ] Add two nullable columns directly to `Event` (`src/EnergyTracker.Domain/Event.cs`): `CorrelationDirection` (`string?`, "Bump"|"Dip") and `CorrelationComputedAtUtc` (`DateTimeOffset?`). Both null = "no correlation" (AC #3) — this is the simplest representation and needs no new table/repository, since correlation is 1:1-owned by its Event with no independent lifecycle. Migration via `scripts/add-migration.sh` (both providers).
  - [ ] Add `Task SetCorrelationAsync(Guid eventId, string? direction, DateTimeOffset computedAtUtc, CancellationToken cancellationToken)` to `IEventRepository` + `EventRepository` adapter.
  - [ ] New `CorrelateEvent` use case (Application) — payload carries `EventId`, `HouseholdId`, `OccurredAt`, `Description`. Logic: load `Household`; **if `AiPlausibilityEnabled` is false or `IAiPlausibilityClient` resolves to the no-op (unconfigured), do nothing and return** — this is the *only* place in the whole feature that checks "is AI enabled," per AD-8's explicit anti-hard-branch rule (`project-context.md`: "features must not check 'is AI enabled' and take a different code path"). Otherwise: run Task 3's calculator for the Event's window to get observed direction(s); if none, leave correlation null (AC #3); if a deviation exists, call `IAiPlausibilityClient.ClassifyAsync(description, observedDirections, ct)` to see whether the Event's inferred expected direction matches; persist the result via `SetCorrelationAsync`. Must never throw out of `ExecuteAsync` — a job-queue retry storm on an AI/network hiccup would itself become a product-availability issue that NFR14 exists to prevent.
  - [ ] Wire `CreateEvent` (`src/EnergyTracker.Application/CreateEvent.cs`) to **unconditionally** enqueue a `JobEnvelope<CorrelateEventPayload>` via `IBackgroundJobQueue` right after `AddAsync` succeeds — no enable/disable check here (that lives entirely in `CorrelateEvent`, per the rule above). This is new wiring: `CreateEvent` currently enqueues nothing at all.
  - [ ] Register the job-queue consumer for `CorrelateEventPayload` (mirror how the existing Smart Plug import job consumer is wired in `Program.cs`/Infrastructure).
  - [ ] Tests: `CorrelateEventTests` (Application, NSubstitute for `IEventRepository`/`IAiPlausibilityClient`/`IHouseholdRepository`) covering enabled+deviation+match, enabled+deviation+no-match, enabled+no-deviation, disabled, unconfigured, AI-client-throws-nothing-propagates. Infrastructure dual-provider test for the new `Event` columns + `SetCorrelationAsync`. Api test confirming `GetEventHistory`/`GET /api/events` now returns the correlation fields.

- [ ] **Task 5 — Frontend inline display (AC: #1, #3, #7)**
  - [ ] Extend `EventDto` (`web/src/lib/event-api.ts`) with `correlationDirection: 'Bump' | 'Dip' | null`.
  - [ ] In `EventRow`/`events-card.tsx`, render the correlation **inline in the same row** — never a separate step/view (AC #7). No correlation → render nothing extra, no placeholder, no "no match found" state (AC #3, UX-DR14).
  - [ ] Display text is **always one of exactly two fixed, translated strings** — never raw AI output rendered verbatim (this is required by AD-18 i18n and UX-DR17's plain-language/no-false-precision voice-and-tone rule, even though it's not spelled out as its own AC): `trendHistory.eventsCard.correlation.bump` → *"Roughly matches the bump seen."* and `.dip` → *"Roughly matches the dip seen."* (exact copy from EXPERIENCE.md's Voice and Tone Do/Don't table — do not paraphrase). Add both keys to `en-US` and `de-DE` in the same commit.
  - [ ] Component tests: present (bump), present (dip), absent — confirm absent renders cleanly with no extra markup.

- [ ] **Task 6 — Cross-cutting verification (AC: all)**
  - [ ] Full backend suite green per-project (Domain/Application/Infrastructure/Api/Architecture — MTP runner rejects a combined multi-project run), full frontend suite green, `tsc -b`, `oxlint`, `vite build` all clean.
  - [ ] Live verification gate (project convention — blocks review→done, does not defer): confirm end-to-end in a real render with the household AI toggle **off** (no correlation appears, no console errors) and, if a reachable AI backend is available in the dev environment, **on** (a real classification round-trip completes and renders one of the two fixed strings). If no backend is reachable in this sandboxed environment, explicitly say so rather than claiming the "on" path was verified (same gap the project has already hit for OIDC in earlier stories).
  - [ ] File anything punted into `_bmad-artifacts/implementation/deferred-work.md` under a `## Deferred from: code review of story-6.3 (date)` heading, with `summary`/`evidence`/`[file:line]`, matching the existing format.

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

{{agent_model_name_version}}

### Debug Log References

### Completion Notes List

### File List
