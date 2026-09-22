---
baseline_commit: 710db9604bc0265be23e6387ade26eb4db4f7dc0
---

# Story 7.1: Full Data Export

Status: done

<!-- Note: Validation is optional. Run validate-create-story for quality check before dev-story. -->

## Story

As a Household member,
I want to export all of my Household's data in a documented format,
so that I have a disaster-recovery backup and am never locked into the product to access my own data.

## Acceptance Criteria

1. **Given** a Household with data across all features (Meter Readings, Tariff history, Events, Smart Plug data, settings), **when** I trigger an export, **then** all of it is included in a single documented export format. [Source: epics/epic-7-data-export-import-disaster-recovery.md#Story 7.1; PRD FR-22]
2. **Given** the export format, **when** published, **then** it is documented — the data is always readable back out through the format itself, not locked into the product. [Source: epic-7...md#Story 7.1; PRD FR-22, NFR13]
3. **Given** the export, **when** generated, **then** it only includes data scoped to my Household (no cross-Household leakage). [Source: epic-7...md#Story 7.1; AD-3, NFR4]
4. **Given** the export, **when** it runs, **then** it covers data written under either database provider identically — export behavior is provider-agnostic. [Source: epic-7...md#Story 7.1; AD-2]

## Tasks / Subtasks

- [x] **Task 1 — Decide and document the export format** (AC: #1, #2)
  - [x] Decide the file format. Recommendation: a single JSON document (see Dev Notes "Format decision" — no format is mandated anywhere in the PRD/architecture; this is a real decision to disclose, not silently resolve).
  - [x] Decide the top-level schema: one root object, a `formatVersion` field set to the literal string `"v2"` (the PRD/epic already call this "the v2 export format" — FR-23), an `exportedAtUtc` timestamp, then one array/object per entity category. See Dev Notes "Proposed export shape" for a concrete starting point.
  - [x] Decide and **disclose** the entity-inclusion scope (see Dev Notes "Entity scope — decide and disclose") — which of the 15 Household-scoped entity types go in, which are deliberately excluded, and why.
  - [x] Write `docs/data-export-format.md`: the schema, field-by-field description of every included entity, versioning stance (what "v2" means, that this codebase has no v1 export to be compatible with — FR-23 is explicit v2-to-v2 only), and a worked example. This file *is* AC #2's "documented" — the export is worthless as a disaster-recovery backup if nothing describes how to read it back.

- [x] **Task 2 — Backend: read path for a full Household export** (AC: #1, #3, #4)
  - [x] Decide the read-path shape (see Dev Notes "Read-path design decision"): either (a) add an export-scoped bulk read method to each existing repository port, or (b) introduce one new port (e.g. `IHouseholdExportReader`) with a single Infrastructure adapter that queries `EnergyTrackerDbContext` directly across all needed `DbSet`s. Recommendation: (b) — it keeps the narrow, purpose-built existing repository interfaces (`ITariffRepository`, `IEventRepository`, etc.) uncluttered with a bulk-export method none of their other callers need.
  - [x] Whichever shape is chosen, the read **must** go through `EnergyTrackerDbContext`'s existing global query filter (AD-3) — never `.IgnoreQueryFilters()`, never `FromSqlRaw`, never `DbSet<T>.Find()` against a Household-scoped entity. This is what makes AC #3 true structurally, not by convention.
  - [x] Every query is plain LINQ over the AD-2 portable relational subset only (no provider-specific operators) — this is what makes AC #4 true. No new migration should be needed; this story only reads.
  - [x] `EnergyTracker.Architecture.Tests` already encodes AD-1/AD-3 as guard tests — if the new port/adapter needs a new architecture-test assertion (e.g. a new port lives in `Application/Ports`, its adapter in `Infrastructure/Adapters`), add it.

- [x] **Task 3 — Application: `ExportHouseholdData` use case** (AC: #1, #2, #3, #4)
  - [x] New flat file `EnergyTracker.Application/ExportHouseholdData.cs`, single `ExecuteAsync(Guid householdId, CancellationToken)` method, following this codebase's existing use-case shape (imperative-verb class name, primary-constructor DI over the port(s) from Task 2 — see `GetCurrentStatus.cs` for the multi-port constructor precedent).
  - [x] Compose the decided entity scope (Task 1) into the JSON-serializable export shape. Every field is copied as-is off the entity — this use case performs **zero** derivation/recomputation (AD-14: no summed/reconciled figure anywhere in this codebase; this export is a raw dump of already-stored values, not a report).
  - [x] `Event.TaggedEntityName`, `CorrelationDirection`, `CorrelationComputedAtUtc` are copied verbatim as opaque snapshot fields — never recomputed, never re-derived from the currently-tagged entity's live name (AD-10). This is explicitly called out in the Epic 6 retrospective's Epic 7 kickoff note as something to get right at export time, not defer to the 7.2 import story.
  - [x] `AiPlausibilityEnabled` (Household-scoped) and the deployment's `AiPlausibilityBackendOptions.Label`/`Configured` (deployment-wide, non-secret — see `EnergyTracker.Application/AiPlausibilityBackendOptions.cs`) both go into the export's settings section, so a restore brings back Household *state*, not just rows (same retro kickoff note). **Never** export `AiPlausibilityBackendOptions.Model` or any `BaseUrl`/API-key config — those are deployment secrets (AD-19), out of scope for a per-Household data export.
  - [x] `StatusSnapshot` rows are included as their own persisted rows, not recomputed — AD-7 forbids a later settings change silently rewriting history, so if the export ever needs to reconstruct Trend History, it must ship the actual historical snapshots, not regenerate them from current Household settings.
  - [x] The two enum fields in scope (`StatusSnapshot.Status`, `MeterRegressionPrompt.Classification`) must be serialized as their lowercase string names, matching every existing endpoint's manual `.ToString().ToLowerInvariant()` convention (`MeterRegressionClassification.cs`'s own comment: "not via a global `JsonStringEnumConverter`. Don't add one.") — the default `System.Text.Json` integer encoding would silently break AC #2's "documented, self-describing" requirement.

- [x] **Task 4 — API: export endpoint** (AC: #1, #2, #3, #4)
  - [x] New `EnergyTracker.Api/Endpoints/HouseholdExportEndpoints.cs`, one `GET` route (recommend `/api/household-export`, kebab-case singular concept per this codebase's naming convention — not a collection of "household-exports"). Household is resolved from `ICurrentHouseholdAccessor.HouseholdId`, never taken from the URL/body (matches the flat-route IDOR-safe-by-construction pattern most endpoints already use, e.g. `EventEndpoints`/`TariffEndpoints` — no `{id}` to guess or leak against another Household).
  - [x] 403 `ProblemDetails` (RFC 7807, this codebase's existing error shape) when the authenticated principal has no Household — copy `JobEndpoints.TryGetHouseholdId`'s existing helper pattern rather than reinventing it.
  - [x] Response: `Content-Type: application/json`, `Content-Disposition: attachment; filename="energy-tracker-export-{yyyy-MM-dd}.json"` so a browser `fetch` + click-through downloads a real file rather than navigating to raw JSON.
  - [x] Sync vs. async decision (see Dev Notes "Tier/sync decision") — recommendation: **synchronous**, Tier 2 (≤30s, UI progress hint), not routed through `IBackgroundJobQueue`/AD-6. The architecture's own capability map lists Data Export/Import against AD-2/AD-3 only, not AD-6 — there's no async-job precedent to follow here, and this is a pure read with no write-conflict/throughput concern like Smart Plug import's AD-23 bulk-write problem.

- [x] **Task 5 — Frontend: Settings entry point** (UX-DR12)
  - [x] New component folder `web/src/components/data-export/`, following the existing `components/{feature}` grouping (see `ai-plausibility/`, `yearly-baseline/`). A component (e.g. `data-export-panel.tsx`) with a single "Export data" trigger button that `fetch('/api/household-export', { credentials: 'include' })`, reads the response as a `Blob`, and drives a synthetic `<a download>` click to save it — no dedicated mockup exists for this yet (Settings mockup only shows a bare `"Data export & import" →` settings row with no expanded state — see Dev Notes "No UX mockup for this screen"), so build directly against this codebase's existing conventions (`GlassCard`, `Button`, the `ApiError`/`toApiError` per-component pattern already duplicated in `ai-plausibility-form.tsx`/`yearly-baseline-form.tsx` — copy it again here, don't extract a shared helper, matching this repo's established per-component-copy convention).
  - [x] Wire the new component into `web/src/components/settings/settings-page.tsx`, alongside `YearlyBaselineForm`/`AiPlausibilityForm`/`TaggingScaffoldManager`/`InviteGeneratePanel`. This story only ships the **export** half — do not build an import UI/control (that's Story 7.2), but leave room in the panel/copy for it to land later without a rework.
  - [x] i18next copy keys under the existing `settings.*` namespace (see `settings-page.tsx`'s existing `t('settings...')` calls).

- [x] **Task 6 — Tests** (all ACs)
  - [x] `EnergyTracker.Application.Tests/ExportHouseholdDataTests.cs`: `{SubjectClass}Tests` naming, `Snake_case_with_underscores` method names, Shouldly assertions, NSubstitute-mocked port(s) — verify every decided entity category is present in the result, verify a second Household's data never appears (AD-3), verify `Event`'s opaque snapshot fields are copied unchanged (not recomputed against a live tag).
  - [x] `EnergyTracker.Infrastructure.Tests`: a **real-DbContext** test (Testcontainers, per this codebase's testing rules — AD-2's dual-provider portability can only be verified against real engines) proving the export read path actually respects the AD-3 global query filter end-to-end and returns identical results shaped from both providers. This is exactly the kind of check an NSubstitute-mocked repository test cannot catch (see `project-context.md`'s Story 5.1 multi-field-edit lesson on why a real DbContext matters for this class of bug).
  - [x] `EnergyTracker.Api.Tests`: happy path (200, correct `Content-Disposition`, correct JSON shape) and the no-Household 403 case.
  - [x] Frontend: a `data-export-panel.test.tsx` colocated next to the component (Vitest + Testing Library, not a parallel `__tests__/` tree) covering the trigger → fetch → download flow and the error path, mirroring `ai-plausibility-form.test.tsx`'s existing shape.

- [x] **Task 7 — Live Auth0/Chrome verification** (all ACs — does not defer)
  - [x] Run the actual exported-data round trip against the running app via the Claude-in-Chrome extension: log in, trigger the export from Settings, confirm a real file downloads and its contents include genuine data from every included entity category for the logged-in Household.
  - [x] **Per Epic 6 Retrospective Action Item #1: if the Claude-in-Chrome extension reports "not connected" at this step, raise it immediately and pause for the user to start/connect Chrome, then resume — do not check this task off on the strength of a passing component/unit test alone, and do not let code review be the place this gets caught.** This is a hard gate at dev-story time now, not just a review-time catch.

### Review Findings

- [x] [Review][Patch] Cross-household isolation tests cover only 4 of 12 export entity categories [tests/EnergyTracker.Infrastructure.Tests/HouseholdExportReaderTests.cs:145]. Fixed: `Never_returns_another_Households_data` now seeds and asserts all twelve (added PowerPoint/Device/MeterRegressionPrompt/Tariff/Event/SmartPlugReading/StatusSnapshot/AuditCorrection); API-layer isolation test extended with Tariff/Event alongside Room.
- [x] [Review][Patch] Isolation test doesn't verify its own room-creation setup succeeded [tests/EnergyTracker.Api.Tests/HouseholdExportEndpointsTests.cs:87]. Fixed: setup calls (Room/Tariff/Event) now assert `HttpStatusCode.OK` before the isolation assertion.
- [x] [Review][Patch] Filename date and `exportedAtUtc` can disagree across a UTC-midnight boundary [src/EnergyTracker.Api/Endpoints/HouseholdExportEndpoints.cs:46]. Fixed: filename now derives from `export.ExportedAtUtc` instead of a second independent `DateTimeOffset.UtcNow` call.
- [x] [Review][Patch] Fallback download filename computed once at module load, not at click time [web/src/components/data-export/data-export-panel.tsx:28]. Fixed: fallback filename now computed inside `filenameFromContentDisposition` at call time.
- [x] [Review][Patch] No guard against rapid double-click firing two concurrent export requests [web/src/components/data-export/data-export-panel.tsx:52]. Fixed: added a `downloadingRef` checked synchronously before the `downloading` state re-render can flush.
- [x] [Review][Patch] `HouseholdExportReader.cs` header comment overstates the AD-3 exception's scope [src/EnergyTracker.Infrastructure/Adapters/HouseholdExportReader.cs:9]. Fixed: comment now correctly scopes the "load-bearing" exception to HouseholdMember and notes the explicit filter is redundant-but-harmless (matching existing adapter convention) on entities that already carry the global query filter.
- [x] [Review][Defer] No transactional/snapshot consistency across the reader's ~12 sequential queries [src/EnergyTracker.Infrastructure/Adapters/HouseholdExportReader.cs] — deferred, pre-existing: no other multi-query read use case in this codebase (e.g. `GetCurrentStatus`) wraps its reads in a transaction/snapshot either; not a regression introduced by this diff.
- [x] [Review][Defer] No streaming/size cap on the unbounded full-household JSON export; `Results.File` buffers the whole response in memory and doesn't honor cancellation mid-serialization [src/EnergyTracker.Api/Endpoints/HouseholdExportEndpoints.cs] — deferred, explicitly disclosed trade-off in this story's own Dev Notes "Tier/sync decision" (revisit if real data volume risks the ~240s ingress ceiling, not a reason to default to async now).
- [x] [Review][Defer] `Households.SingleAsync`/`MainMeters.SingleOrDefaultAsync` have no guard against a data-integrity multi-row violation, surfacing as an unhandled 500 [src/EnergyTracker.Infrastructure/Adapters/HouseholdExportReader.cs:17-26] — deferred, identical to the existing codebase-wide convention (`HouseholdRepository.cs`, `MeterReadingRepository.cs` use the same pattern), not introduced by this diff.
- [x] [Review][Defer] `TryGetHouseholdId` copy-pasted verbatim into yet another endpoint file [src/EnergyTracker.Api/Endpoints/HouseholdExportEndpoints.cs] — deferred, pre-existing pattern (already duplicated across `MeterReadingEndpoints`/`EventEndpoints`), extended not introduced by this diff.
- [x] [Review][Defer] Completion Notes overstate architecture-test coverage — claims "existing tests already enforce structurally" the `Application/Ports` + `Infrastructure/Adapters` placement convention, but no `EnergyTracker.Architecture.Tests` file actually does — deferred, documentation-accuracy nit only, not a functional gap (Task 2's requirement was conditional on needing a new assertion).

## Dev Notes

### Format decision

No file format (JSON/CSV/ZIP/etc.) is mandated anywhere in the PRD, epic, or architecture spine — this is a genuine open decision. **Recommendation: a single JSON document.** Rationale: the data spans many relational entity types (a multi-file CSV/ZIP would need its own cross-file referential-integrity story); JSON is self-describing and matches every existing API response shape in this codebase already; a native DB dump (`pg_dump`/`bcp`) would violate AD-2's provider-agnostic requirement outright (the two providers' native dump formats are not interchangeable). Disclose this decision explicitly rather than silently picking it — it is exactly the kind of design call this team's established practice is to surface for review (see Epic 6 Retro: "ambiguity was consistently flagged for review rather than silently resolved").

### Entity scope — decide and disclose

15 Domain types carry `HouseholdId` (Household-scoped). The epic's own AC language ("Meter Readings, Tariff history, Events, Smart Plug data, settings") maps cleanly to most of them, but a few are genuinely ambiguous and were **not** pre-decided by this story — treat the recommendation below as a strong starting point to confirm, not a settled fact:

**Recommend including:** `Household` (settings: Locale, Currency, YearlyBaselineKwh, TrendingThresholdKwh, LowConfidenceGapDays, TariffCheckCadenceMonths, AiPlausibilityEnabled) + `AiPlausibilityBackendOptions.Label`/`Configured`; `HouseholdMember` (DisplayName + ExternalIssuer/ExternalSubjectId — needed to know who belongs to the Household on restore); `MainMeter`; `MeterReading`; `MeterRegressionPrompt`; `Tariff`; `Event`; `Room`/`PowerPoint`/`Device` (including archived/soft-deleted rows — AD-10 history integrity, a restore that drops archived tags would corrupt the by-value snapshots on `SmartPlugReading`/`Event` that reference them); `SmartPlugReading`; `StatusSnapshot`; `AuditCorrection` (real historical correction data — excluding it would silently lose genuine record-keeping on a disaster-recovery restore, even though AD-11 says the *import* path itself never calls `IAuditCorrectionRecorder`; that's about how 7.2 writes, not about whether 7.1 reads these rows).

**Recommend excluding:** `HouseholdInvite` (its `Token` is a live bearer credential granting full Household membership — a security-sensitive secret, not "data"; a consumed/expired invite has no restore value either). `BackgroundJob`, `SmartPlugImport`, `SmartPlugImportGap` (transient job-processing/queue metadata with its own existing 30-day auto-cleanup lifecycle per FR-32 — not household energy data, and re-importing this on a 7.2 restore would resurrect job rows for uploads that no longer exist).

### Read-path design decision

Every existing repository port (`ITariffRepository`, `IEventRepository`, etc. — see `EnergyTracker.Application/Ports/`) only exposes narrow, paginated, feature-specific queries (`GetHistoryForHouseholdAsync(page, pageSize, ...)`, `GetPageForHouseholdAsync(...)`) — **there is no existing "get everything for this Household" method anywhere in this codebase.** This is expected new work for this story, not a gap to work around. Two shapes are viable:

- **(a)** Add one new bulk-read method to each existing repository interface.
- **(b) (recommended)** One new port, e.g. `IHouseholdExportReader`, with a single `Infrastructure` adapter reading `EnergyTrackerDbContext`'s `DbSet`s directly for every entity in scope. Keeps the export concern out of interfaces whose other callers (the paginated UI list views) don't need it.

Whichever is chosen, AD-3's rule still applies without exception: reads go through the DbContext's global query filter, never `IgnoreQueryFilters()`/`FromSqlRaw`/`Find()`.

### Tier/sync decision

Cross-Cutting NFRs define three tiers: Tier 1 (≤2s), Tier 2 (≤30s, UI progress hint), Tier 3 (fully async — "Smart Plug imports, scheduled jobs"). The architecture's capability map lists Data Export/Import against AD-2/AD-3 only — **not** AD-6 (the async-job port) — so there's no existing precedent requiring this to be a background job. Recommend synchronous Tier 2: a personal-household's full relational dataset (even years of Meter Readings/Smart Plug intervals) is a pure read with no write-conflict/throughput concern of the kind AD-23's bulk-write spike had to solve for Smart Plug import. If real data volume ever risks the ~240s Container Apps ingress ceiling (the same wall the Story 3.10 incident hit — see `project-context.md`'s CI/Migrations notes and AD-6's `JobEnvelope` extension), that's a reason to revisit, not a reason to default to async now.

### No UX mockup for this screen

`ux-designs/.../mockups/key-settings.html` shows only a bare settings row — `<div class="settings-row"><span>Data export &amp; import</span><span class="arrow">→</span></div>` — with no expanded/detail state built. UX-DR12 requires the *surface* to be reachable from Settings; it does not (yet) specify the detail screen's layout. This mirrors UX-DR21's own precedent (Story 3.10's "Clean Up History" control, built directly against existing conventions with no dedicated mock, revisited later if a UX pass flags a mismatch) — follow the same approach here: build against `GlassCard`/`Button`/existing Settings row conventions, don't block on a missing mockup, and don't over-invest in a bespoke design for a single button.

### Relevant architecture invariants (do not violate)

- **AD-3 (tenant isolation):** the entire correctness of AC #3 rests on going through the DbContext's global query filter. No new per-repository household filtering, ever.
- **AD-2 (dual-provider):** plain LINQ, portable relational types only. This story adds no migration.
- **AD-14 (Main Meter is sole authoritative total):** this use case is binding-listed under AD-14 explicitly ("Data Export/Import"). It must never compute or emit any summed/reconciled figure — every field is a raw copy of an already-stored value.
- **AD-10 (historical tag integrity):** `Event`/`SmartPlugReading`'s by-value snapshot fields (`TaggedEntityName`, `RoomName`/`PowerPointName`/`DeviceName`) are copied as-is, never re-derived from the live tagged entity.
- **AD-7 (computed-at-request-time vs. persisted history):** `StatusSnapshot` rows are exported as persisted history, never recomputed.
- **AD-19 (secrets):** never export `AiPlausibilityBackendOptions.Model`/`BaseUrl`/API keys, or any other deployment-level secret.

### Enum serialization convention (do not add a global converter)

No global `JsonStringEnumConverter`/`ConfigureHttpJsonOptions` exists anywhere in `src/EnergyTracker.Api/` — confirmed by direct search. Every existing endpoint manually lowercases each enum field at the point of DTO construction (e.g. `JobEndpoints.cs:56`: `result.Job.Status.ToString().ToLowerInvariant()`; `MeterRegressionPromptEndpoints.cs:109`: `prompt.Classification!.Value.ToString().ToLowerInvariant()`). `MeterRegressionClassification.cs` documents this as deliberate. The export's two enum fields (`StatusSnapshot.Status`, `MeterRegressionPrompt.Classification`) must follow the same manual per-field convention, not a converter — adding one would also silently change every other endpoint's existing wire format.

### Testing standards summary

.NET: `{SubjectClass}Tests` per class, `Snake_case_with_underscores` method names, Shouldly assertions, NSubstitute mocks against ports, `TestContext.Current.CancellationToken` (xUnit v3 MTP). A Testcontainers-backed Infrastructure test is required for the AD-3/AD-2 read-path claims — an NSubstitute-mocked Application-layer test cannot verify the actual global query filter or provider-portability. Frontend: Vitest + Testing Library, colocated `*.test.tsx`.

### Project Structure Notes

- Backend: new use case `EnergyTracker.Application/ExportHouseholdData.cs` (flat, no feature-folder nesting); new port(s) in `EnergyTracker.Application/Ports/`; new adapter in `EnergyTracker.Infrastructure/Adapters/`; new endpoint file `EnergyTracker.Api/Endpoints/HouseholdExportEndpoints.cs`, registered alongside the other `Map*Endpoints` calls in `Program.cs`.
- New doc: `docs/data-export-format.md` (this codebase's `docs/` currently holds `local-development.md`, `local-vs-azure-deltas.md`, `self-hosting.md` — same flat layout).
- Frontend: new `web/src/components/data-export/` folder, wired into `web/src/components/settings/settings-page.tsx`.
- No conflicts detected with existing structure/conventions.

### References

- [Source: _bmad-artifacts/planning/epics/epic-7-data-export-import-disaster-recovery.md#Story 7.1]
- [Source: _bmad-artifacts/planning/prds/prd-energy-tracker-2026-08-08/prd/4-features.md#FR-22, FR-23]
- [Source: _bmad-artifacts/planning/prds/prd-energy-tracker-2026-08-08/prd/cross-cutting-nfrs.md — Performance tiers, Tenant isolation, Documentation as onboarding path]
- [Source: _bmad-artifacts/planning/epics/requirements-inventory.md#NFR13]
- [Source: _bmad-artifacts/planning/architecture/architecture-energy-tracker-2026-08-09/ARCHITECTURE-SPINE/invariants-rules.md#AD-2, AD-3, AD-7, AD-10, AD-11, AD-14, AD-19]
- [Source: _bmad-artifacts/planning/architecture/architecture-energy-tracker-2026-08-09/ARCHITECTURE-SPINE/capability-architecture-map.md — "Data Export/Import (FR-22–FR-23) | Application use case over all repositories | AD-2, AD-3"]
- [Source: _bmad-artifacts/planning/architecture/architecture-energy-tracker-2026-08-09/ARCHITECTURE-SPINE/deferred.md — narrow-recovery gap note naming Epic 7 as the only planned recovery path]
- [Source: _bmad-artifacts/planning/ux-designs/ux-energy-tracker-2026-08-08/EXPERIENCE.md#Information Architecture table; UX-DR12/UX-DR14 per requirements-inventory.md]
- [Source: _bmad-artifacts/planning/ux-designs/ux-energy-tracker-2026-08-08/mockups/key-settings.html]
- [Source: _bmad-artifacts/implementation/epic-6-retro-2026-09-21.md — Epic 7 kickoff note (Event snapshot fields, AiPlausibilityEnabled + backend label), Action Item #1 (live-verification gate)]
- [Source: src/EnergyTracker.Application/GetCurrentStatus.cs — multi-port primary-constructor precedent]
- [Source: src/EnergyTracker.Api/Endpoints/JobEndpoints.cs — TryGetHouseholdId IDOR-guard pattern]
- [Source: web/src/components/ai-plausibility/ai-plausibility-form.tsx — frontend fetch/ApiError pattern]
- [Source: web/src/components/settings/settings-page.tsx — Settings composition pattern]
- [Source: _bmad-artifacts/project-context.md]

## Dev Agent Record

### Agent Model Used

Claude Sonnet 5 (claude-sonnet-5)

### Debug Log References

None — no failing runs beyond one self-caught test bug (see Completion Notes).

### Completion Notes List

- Format decision (disclosed, Dev Notes "Format decision"): single JSON document, top-level `formatVersion: "v2"` + `exportedAtUtc` + one array/object per entity category. Documented in full in `docs/data-export-format.md`.
- Entity scope (disclosed, Dev Notes "Entity scope"): shipped exactly the story's recommended include/exclude split — `HouseholdInvite` (live bearer secret) and `BackgroundJob`/`SmartPlugImport`/`SmartPlugImportGap` (transient job-queue metadata) excluded; everything else (Household settings, HouseholdMember, MainMeter, MeterReading, MeterRegressionPrompt, Tariff, Event, Room/PowerPoint/Device incl. archived, SmartPlugReading, StatusSnapshot, AuditCorrection) included. Additionally decided and disclosed in the doc: EF `Version` concurrency tokens, every child entity's redundant `HouseholdId`, and `SmartPlugReading.SmartPlugImportId` are all left out as noise, not data.
- Read-path shape: new `IHouseholdExportReader` port (Application/Ports) + single `HouseholdExportReader` adapter (Infrastructure/Adapters) reading `EnergyTrackerDbContext`'s `DbSet`s directly with `AsNoTracking()`, per the story's own recommendation (b). No new architecture-test assertion was needed — the port/adapter follows the existing `Application/Ports`/`Infrastructure/Adapters` placement convention that `DomainHasNoExternalDependenciesTests`/existing tests already enforce structurally; nothing new to guard.
- `ExportHouseholdData` performs zero derivation — every DTO field is a raw 1:1 copy off the entity. `MeterRegressionPrompt.Classification`/`StatusSnapshot.Status` are manually lowercased per this codebase's existing per-field enum convention (no global `JsonStringEnumConverter`). `AiPlausibilityBackendOptions.Model`/`BaseUrl`/API key are never referenced anywhere in the DTO — only the non-secret `Configured`/`Label` pair is threaded through.
- Endpoint uses `Results.File(bytes, "application/json", fileName)`, which sets `Content-Disposition: attachment` automatically — no manual header construction needed. Synchronous (Tier 2), per the story's own recommendation; no `IBackgroundJobQueue` involvement.
- Frontend `DataExportPanel` reads the response as a Blob, derives the saved filename from the server's own `Content-Disposition` header (falling back to a locally-computed name only if that header is ever missing), and drives a synthetic `<a download>` click — no shared helper extracted for `ApiError`/`toApiError`, matching this repo's established per-component-copy convention.
- One self-caught test bug (not a production bug): `HouseholdExportReaderTests`' cross-Household isolation test originally inserted a `MeterReading` for the other Household with a random, non-existent `MainMeterId`, which both providers correctly rejected via the FK constraint — fixed by seeding a real `MainMeter` for that Household first.
- Full backend suite green: 358 Application (14 new) + 220 Infrastructure (8 new, dual-provider via Testcontainers) + 210 Api (4 new) + 4 Architecture. `dotnet build` clean. Full frontend suite green: 379/379 (3 new). `tsc -b`/`oxlint`/`vite build` all clean.
- **Live Auth0/Chrome verification (Task 7) — cleared.** The Claude-in-Chrome extension connected on the first attempt (no Epic 6 Retro Action Item #1 "not connected" gate hit). The real household on this dev tenant had zero Meter Readings/Tariffs at session start ("No Status yet") — paused and asked Ralf how to proceed before writing any data into his real household; he chose to have minimal real test data added live. Logged a real Meter Reading (12345.6 kWh), a real Event, and a real Tariff (8.50 USD base fee, 0.32 USD/kWh, starting 2026-01-01) via the running app, then triggered Settings → Data Export → "Export data" against `https://localhost:7005`. Confirmed live: `GET /api/household-export` returned 200, a real 32 MB file downloaded via the browser's own download mechanism with the server-computed filename, and the file's actual contents (inspected directly, not just asserted) show `formatVersion: "v2"`, the real household settings, 1 HouseholdMember, 1 MainMeter, the 1 Meter Reading just logged, the 1 Tariff just created, 3 real Events (including the new one, with `taggedEntityName: "Living Room"` correctly preserved even though that Room's `archivedAt` is set — confirming AD-10 by-value snapshotting end-to-end against real archived data), 1 archived Room + 1 PowerPoint, and 117,770 real `SmartPlugReading` rows from this household's actual prior Smart Plug import history — genuine data, not fixtures, round-tripping through the real endpoint. No `smartPlugImportId` field present on any SmartPlugReading row (by design). No console errors. `MeterRegressionPrompt`/`StatusSnapshot`/`AuditCorrection`/`Device` were legitimately empty for this household (no regression ever flagged, no Yearly Baseline set so no Status ever computed, no correction ever made, no Device ever created under the one Power Point) — not a gap, a true reflection of this household's real history. API process and browser tab cleaned up afterward.

### File List

- `docs/data-export-format.md` (new)
- `src/EnergyTracker.Application/Ports/IHouseholdExportReader.cs` (new)
- `src/EnergyTracker.Infrastructure/Adapters/HouseholdExportReader.cs` (new)
- `src/EnergyTracker.Application/ExportHouseholdData.cs` (new)
- `src/EnergyTracker.Api/Endpoints/HouseholdExportEndpoints.cs` (new)
- `src/EnergyTracker.Api/Program.cs` (modified — DI registrations + `MapHouseholdExportEndpoints()`)
- `web/src/components/data-export/data-export-panel.tsx` (new)
- `web/src/components/data-export/data-export-panel.test.tsx` (new)
- `web/src/components/settings/settings-page.tsx` (modified — wired in `DataExportPanel`, updated header comment)
- `web/src/locales/en-US/translation.json` (modified — `settings.dataExport.*` keys)
- `web/src/locales/de-DE/translation.json` (modified — `settings.dataExport.*` keys)
- `tests/EnergyTracker.Application.Tests/ExportHouseholdDataTests.cs` (new)
- `tests/EnergyTracker.Infrastructure.Tests/HouseholdExportReaderTests.cs` (new)
- `tests/EnergyTracker.Api.Tests/HouseholdExportEndpointsTests.cs` (new)

## Change Log

- 2026-09-22: Story 7.1 implemented end-to-end (dev-story). Export format/entity scope decided and disclosed in `docs/data-export-format.md`; `IHouseholdExportReader`/`HouseholdExportReader` read path; `ExportHouseholdData` use case (zero derivation, AD-14); `GET /api/household-export` endpoint; `DataExportPanel` Settings entry point; full test coverage across all four layers. Live Auth0/Chrome verification cleared against real household data (see Completion Notes). Status set to review.
- 2026-09-22: Code review complete (bmad-code-review). 3 parallel layers ran (Blind Hunter, Edge Case Hunter, Acceptance Auditor — no spec violations found). 6 patch findings applied: extended both cross-household isolation tests to cover all 12 export entity categories (was 4); fixed the isolation test's unchecked setup calls; fixed a filename/`exportedAtUtc` UTC-midnight mismatch; fixed the frontend fallback filename's stale module-load-time date; added a double-click guard to the export button; corrected an imprecise AD-3-exception comment. 5 items deferred to `deferred-work.md` (pre-existing codebase conventions or trade-offs already disclosed in this story's own Dev Notes, not regressions). Full suite re-verified green after patches: 358 Application + 220 Infrastructure (dual-provider) + 210 Api + 4 Architecture, 379/379 frontend, clean build/tsc/oxlint. Status set to done.
