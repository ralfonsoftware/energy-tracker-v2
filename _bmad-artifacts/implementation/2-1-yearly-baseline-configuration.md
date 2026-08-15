---
baseline_commit: 86d1e0f9f3715f4506c31483e88862e2bb5d65f3
---

# Story 2.1: Yearly Baseline Configuration

Status: done

<!-- Note: Validation is optional. Run validate-create-story for quality check before dev-story. -->

## Story

As a Household member,
I want to set and later edit my Household's Yearly Baseline,
so that Pattern Detective has a target to measure my consumption pace against.

## Acceptance Criteria

1. **Given** onboarding or Settings **When** I set a Yearly Baseline **Then** household-size presets (1 person ≈ 1500 kWh, 2 ≈ 2500 kWh, 3 ≈ 3500 kWh, 4 ≈ 4250 kWh) are offered as starting suggestions, never silently applied as a default (FR-2, AD-15)
2. **Given** an existing Yearly Baseline **When** I change it **Then** the change takes effect going forward only — it never retroactively rewrites past Status history (FR-2, NFR9)
3. **Given** the Yearly Baseline value **When** stored **Then** it is a Household-scoped config row, never a literal in code (AD-15)
4. **Given** two Household members editing the Yearly Baseline at the same time **When** both submit **Then** the second writer receives a 409 conflict rather than silently overwriting the first (AD-4, NFR10)

## Tasks / Subtasks

- [x] Task 1: Domain & persistence — add the config field and a concurrency token to `Household` (AC: #3, #4)
  - [x] In `src/EnergyTracker.Domain/Household.cs`, add `public decimal? YearlyBaselineKwh { get; set; }` (nullable — a Household may not have set one yet) and `public int Version { get; set; }`. `Household` has **no concurrency token today** — `HouseholdInvite.cs` is the only existing precedent for the AD-4 pattern; this story introduces it on `Household` itself.
  - [x] In `src/EnergyTracker.Infrastructure/Configurations/HouseholdConfiguration.cs`, map `YearlyBaselineKwh` as a nullable decimal column and mark `Version` with `.IsConcurrencyToken()` — copy the exact line from `HouseholdInviteConfiguration.cs` (`builder.Property(i => i.Version).IsConcurrencyToken();`).
  - [x] Generate the migration via `scripts/add-migration.sh AddYearlyBaselineAndVersionToHousehold` — never `dotnet ef migrations add` directly against one provider project (AD-2; the script adds it to both provider projects in the same commit).

- [x] Task 2: Application use case (AC: #1, #2, #3, #4)
  - [x] Add `src/EnergyTracker.Application/SetYearlyBaseline.cs`: `public class SetYearlyBaseline(IHouseholdRepository repository)` (flat file, one use case per file, primary-constructor DI — mirror `CreateHousehold.cs`), with a single `ExecuteAsync(Guid householdId, decimal yearlyBaselineKwh, int expectedVersion, CancellationToken cancellationToken)`. Throw `HouseholdValidationException` when `yearlyBaselineKwh <= 0`.
  - [x] Add `Task<Household> UpdateYearlyBaselineAsync(Guid householdId, decimal yearlyBaselineKwh, int expectedVersion, CancellationToken cancellationToken)` to `IHouseholdRepository` (`src/EnergyTracker.Application/Ports/IHouseholdRepository.cs`) and implement it in `HouseholdRepository.cs`.
  - [x] **Exact mechanism (do not substitute a manual version check):** after loading the `Household`, set `dbContext.Entry(household).Property(h => h.Version).OriginalValue = expectedVersion;` — this is what makes EF's `SaveChangesAsync` compare `expectedVersion` (the caller's known value) against the DB, not whatever the freshly-loaded entity already has. Then set `household.YearlyBaselineKwh` and **increment** `household.Version++` before saving — AD-4 requires the token to change on every update, or it never actually guards anything (see the reasoning comment at `HouseholdRepository.cs` in `AcceptInviteAsync`, right above `invite.Version++`). A hand-rolled `if (household.Version != expectedVersion) throw ...` check is **not** an acceptable substitute — it never touches `DbUpdateConcurrencyException` and contradicts the mechanism this story is standardizing on.
  - [x] On `catch (DbUpdateConcurrencyException)`, throw a new typed exception, e.g. `HouseholdConcurrencyConflictException`, following `Household`'s actual existing exception family — `HouseholdValidationException`, `HouseholdAlreadyExistsException`, `HouseholdInviteNotFoundException`, `HouseholdInviteExpiredOrConsumedException` (not the unrelated `*ArchivedException` family, which belongs to Room/PowerPoint/Device soft-delete, AD-10). This becomes the second place in the codebase allowed to know about `DbUpdateConcurrencyException` (see the AD-1 trap-note precedent already documented next to `AcceptInviteAsync`'s catch block).
  - [x] `Household` is the tenant root and carries **no AD-3 query filter** on itself (see the comment on `AcceptInviteAsync`'s final line in `HouseholdRepository.cs`) — don't add an `ICurrentHouseholdAccessor` filter to this lookup; that pattern is for household-*scoped child* entities, not `Household` itself.
  - [x] Register the new use case in `src/EnergyTracker.Api/Program.cs`: `builder.Services.AddScoped<SetYearlyBaseline>();`, next to the existing `AddScoped<CreateHousehold>()`/`AddScoped<CreateHouseholdInvite>()`/`AddScoped<AcceptHouseholdInvite>()` lines — every use case is registered here; skipping this step fails DI resolution when wiring the Task 3 endpoint.

- [x] Task 3: API endpoint (AC: #1, #4)
  - [x] In `src/EnergyTracker.Api/Endpoints/HouseholdEndpoints.cs`, add a **brand-new** `GET /households/{id}` route (no `GET` exists on this file today — only `POST /households`) returning `YearlyBaselineKwh` and `Version` alongside the existing `Locale`/`Currency` fields (extend `HouseholdResponse` or add a dedicated response record) — the frontend form needs this to read the live server state before editing or retrying after a conflict.
  - [x] Add `PUT /households/{id}/yearly-baseline` accepting `{ yearlyBaselineKwh: decimal, version: int }` (kebab-case route per `consistency-conventions.md`), calling `SetYearlyBaseline.ExecuteAsync`, returning the updated household (including the new `Version`) on success.
  - [x] Catch `HouseholdConcurrencyConflictException` → `Results.Problem(detail: ex.Message, statusCode: StatusCodes.Status409Conflict)`, mirroring the existing `HouseholdAlreadyExistsException` → 409 handling already in this file. Note this sends only `ex.Message`, not the full current server state — that matches every existing 409 in this codebase (`HouseholdAlreadyExistsException`, the tagging-scaffold archived-parent conflicts), even though `consistency-conventions.md` describes the ideal as "409 with current server state." Follow existing precedent (message only); the frontend's Task 4 refetch covers getting the current state. RFC 7807 `ProblemDetails` only — no ad hoc error shape.

- [x] Task 4: Frontend Yearly Baseline form (AC: #1, #2, #4)
  - [x] New folder `web/src/components/yearly-baseline/` — its own feature folder, per the existing `household-invite`/`tagging-scaffold` convention (new feature UI doesn't get dumped into `settings/` or `components/ui`).
  - [x] `yearly-baseline-form.tsx`: fetch `GET /households/{id}` on mount to read the live `YearlyBaselineKwh`/`Version`. `App.tsx`'s `ready` session state (`CreatedHousehold`) only carries `{ id, locale, currency }` — don't rely on it for the concurrency-sensitive `Version`.
  - [x] Render household-size preset buttons/chips (1p≈1500, 2p≈2500, 3p≈3500, 4p≈4250 kWh) that **fill the numeric input on click only** — never auto-apply or auto-submit (AC #1/AD-15 is explicit these are suggestions, not defaults). Treat the four figures as plain frontend constants — there is no backend endpoint for presets; only the chosen final value is persisted.
  - [x] Submit `PUT` with `{ yearlyBaselineKwh, version }`. Reuse the `ApiError`/`toApiError` pattern from `tagging-scaffold-manager.tsx` to surface `ProblemDetails.detail` instead of a generic error message (a Story 1.9 review lesson).
  - [x] On a 409 response: don't retry blindly. Refetch `GET /households/{id}` for the current server value/Version, surface a conflict message, and require the user to resubmit against the fresh version (NFR10 — no silent overwrite).
  - [x] Guard the submit control against double-submit while a request is in flight (another Story 1.9 review lesson: forms/dialogs must not be resubmittable mid-request).
  - [x] Render `<YearlyBaselineForm />` inside `SettingsPage` (`web/src/components/settings/settings-page.tsx`) as a sibling to `<TaggingScaffoldManager />`, replacing the comment there that currently defers Yearly Baseline to a later story.
  - [x] Use the real design tokens already wired into `web/src/index.css` (Epic 1 retro action item) — not stock shadcn defaults — for any new styling.

- [x] Task 5: i18n (AC: #1, #2, #3, #4)
  - [x] Add a `yearlyBaseline.*` key block to **both** `web/src/locales/en-US/translation.json` and `web/src/locales/de-DE/translation.json` in the same change — key-parity between locales is enforced by convention, not tooling.

- [x] Task 6: Tests (AC: #1, #2, #3, #4)
  - [x] `tests/EnergyTracker.Application.Tests/SetYearlyBaselineTests.cs` — NSubstitute + Shouldly, `Snake_case_with_underscores` method names (mirror `CreateHouseholdTests.cs`'s style). Cover: success path; `yearlyBaselineKwh <= 0` validation; a repository-thrown concurrency exception propagates unchanged.
  - [x] `tests/EnergyTracker.Api.Tests/` — `EnergyTrackerApiFactory`-based integration test against a real Testcontainers-backed database (Postgres and/or SqlServer). Verify: 200 + incremented `Version` on a normal update; **409 on a genuine two-writer race** (two sequential `PUT`s using the same stale `version`) — this must hit a real DB, since AD-4's guarantee is that the concurrency token is actually enforced, not just mocked.
  - [x] `web/src/components/yearly-baseline/yearly-baseline-form.test.tsx` — Vitest + Testing Library, mocked `fetch`. Cover: preset click fills the field without submitting; successful submit updates the shown value; a 409 response triggers the refetch-and-conflict-message flow.

## Dev Notes

- **AC #2 ("forward only, never rewrites Status history") is trivially satisfied by this story alone:** Status computation and `StatusSnapshot` don't exist yet (Story 2.4 builds them). Simply storing the new `YearlyBaselineKwh` value without touching any other rows satisfies this AC now. Flag for Story 2.4: Status computation must read `Household.YearlyBaselineKwh` live at computation time, never snapshot/cache it into a `MeterReading` row, or a later baseline edit would retroactively change history through the back door.
- **No UX mock exists for this screen.** `EXPERIENCE.md` lists Settings/Onboarding as "spine-only, no rendered mock" for Yearly Baseline. Use standard shadcn primitives (`Input`, `Button`, `Label`) unmodified, per `DESIGN/components.md`'s "don't customize what doesn't need customizing" — mirror `household-creation-form.tsx`'s form shape and `tagging-scaffold-manager.tsx`'s `ApiError`/fetch conventions instead of inventing new ones.
- **No conflict-handling UX convention exists in the design docs** — the 409 flow (refetch + message + require resubmit) in Task 4 is this story's own reasonable interpretation of NFR10's "never silently lose an update," not a documented design spec. Keep it simple (no merge UI).
- Public use-case class gets a single-line `/// <summary>` naming the ACs it satisfies, per project convention (see `CreateHousehold.cs`'s doc comment for the exact style).

### Project Structure Notes

- No conflicts with the unified project structure. `Household.cs`, `HouseholdConfiguration.cs`, `HouseholdRepository.cs`, `IHouseholdRepository.cs`, and `HouseholdEndpoints.cs` are all extended in place, following existing patterns exactly — no new project, port, or adapter type is introduced.
- One genuine gap this story fills: `Household` gaining its first `Version` column. This is expected growth of an existing pattern (AD-4 already names "Household settings" as bound by it), not a variance from the architecture.

### References

- [Source: _bmad-artifacts/planning/epics/epic-2-meter-reading-pattern-detective-status-core.md#Story-2.1] — canonical Story 2.1 statement and AC text (reproduced above verbatim).
- [Source: _bmad-artifacts/planning/architecture/architecture-energy-tracker-2026-08-09/ARCHITECTURE-SPINE/invariants-rules.md#AD-15] — "Every household-specific value (Yearly Baseline presets, trending threshold default, currency, Locale) is a `Household`-scoped config row, never a literal in code... Presets... are offered as suggested starting values in the UI, never silently applied as defaults."
- [Source: _bmad-artifacts/planning/architecture/architecture-energy-tracker-2026-08-09/ARCHITECTURE-SPINE/invariants-rules.md#AD-4] — optimistic concurrency via a plain `int Version` EF concurrency token; binds "Meter Reading, Tariff, Household settings" explicitly; `DbUpdateConcurrencyException` → HTTP 409 with current server state.
- [Source: _bmad-artifacts/planning/architecture/architecture-energy-tracker-2026-08-09/ARCHITECTURE-SPINE/invariants-rules.md#AD-2] — dual-provider persistence: portable relational subset only, migrations via `scripts/add-migration.sh` to both provider projects.
- [Source: _bmad-artifacts/planning/architecture/architecture-energy-tracker-2026-08-09/ARCHITECTURE-SPINE/invariants-rules.md#AD-7] — current Status is computed at request time, history persisted via `StatusSnapshot` — basis for the AC #2 forward-only note above.
- [Source: _bmad-artifacts/planning/architecture/architecture-energy-tracker-2026-08-09/ARCHITECTURE-SPINE/consistency-conventions.md] — kebab-case route naming, RFC 7807 `ProblemDetails` error shape, 409-with-current-state convention.
- [Source: _bmad-artifacts/planning/architecture/architecture-energy-tracker-2026-08-09/ARCHITECTURE-SPINE/structural-seed.md] — `Domain.Calculations` folder scope (baseline math, Bonus-Decay Normalizer).
- [Source: _bmad-artifacts/planning/prds/prd-energy-tracker-2026-08-08/prd/4-features.md#FR-2] — Yearly Baseline FR text: presets offered as suggestions during onboarding/Settings; changes apply forward-only.
- [Source: _bmad-artifacts/planning/prds/prd-energy-tracker-2026-08-08/prd/cross-cutting-nfrs.md] — "Recomputation policy" bullet (NFR9: config edits affect calculations going forward only) and "Data integrity under concurrency" bullet (NFR10: concurrent writes never silently lose an update).
- [Source: _bmad-artifacts/planning/prds/prd-energy-tracker-2026-08-08/prd/3-glossary.md] — "Yearly Baseline", "Pattern Detective", "Household" definitions.
- [Source: _bmad-artifacts/planning/ux-designs/ux-energy-tracker-2026-08-08/EXPERIENCE.md#Information-Architecture] — Yearly Baseline appears in Settings and Onboarding/Household Setup; both noted "spine-only, no rendered mock."
- [Source: _bmad-artifacts/planning/ux-designs/ux-energy-tracker-2026-08-08/DESIGN/components.md] — "use standard shadcn components unmodified" guidance for Settings/Onboarding surfaces.
- [Source: _bmad-artifacts/implementation/1-9-room-power-point-device-management.md] — established use-case/testing/i18n conventions; review lessons (surface `ProblemDetails.detail`, guard in-flight submits) carried forward.
- [Source: _bmad-artifacts/implementation/epic-1-retro-2026-08-15.md] — real design tokens now wired into `web/src/index.css`; use them, not shadcn defaults.
- [Source: src/EnergyTracker.Domain/Household.cs] — current entity shape (`Locale`, `Currency`, no `Version` yet).
- [Source: src/EnergyTracker.Domain/HouseholdInvite.cs], [Source: src/EnergyTracker.Infrastructure/Configurations/HouseholdInviteConfiguration.cs] — AD-4 `Version`/`IsConcurrencyToken()` precedent to copy.
- [Source: src/EnergyTracker.Infrastructure/Adapters/HouseholdRepository.cs] — `AcceptInviteAsync`'s increment-then-catch(`DbUpdateConcurrencyException`) pattern; the "tenant root, no AD-3 filter" note.
- [Source: src/EnergyTracker.Application/CreateHousehold.cs], [Source: src/EnergyTracker.Application/Ports/IHouseholdRepository.cs] — use-case class shape and port interface convention.
- [Source: src/EnergyTracker.Api/Endpoints/HouseholdEndpoints.cs] — existing route/`ProblemDetails`/409 handling convention to extend.
- [Source: web/src/components/settings/settings-page.tsx] — insertion point; its own comment defers Yearly Baseline to this story.
- [Source: web/src/components/tagging-scaffold/tagging-scaffold-manager.tsx] — `ApiError`/`toApiError` fetch-and-error pattern to reuse.
- [Source: web/src/App.tsx] — `ready` session state shape (`CreatedHousehold`), confirming it doesn't carry `YearlyBaselineKwh`/`Version`.

## Dev Agent Record

### Agent Model Used

Claude Sonnet 5 (claude-sonnet-5)

### Debug Log References

None — no blocking failures encountered. All test/build runs are captured in Completion Notes.

### Completion Notes List

Ultimate context engine analysis completed - comprehensive developer guide created.

- Task 1: Added `Household.YearlyBaselineKwh` (nullable decimal) and `Household.Version` (AD-4 concurrency token, first on `Household`) plus EF mapping; generated migration `AddYearlyBaselineAndVersionToHousehold` to both provider projects via `scripts/add-migration.sh` (existing rows default `Version` to 0).
- Task 2: Added `SetYearlyBaseline` use case (validates `yearlyBaselineKwh > 0`), `IHouseholdRepository.UpdateYearlyBaselineAsync`, and the `HouseholdRepository` implementation using the `OriginalValue` + `Version++` + `catch (DbUpdateConcurrencyException)` mechanism specified in Dev Notes (mirrors `AcceptInviteAsync`). Added `HouseholdConcurrencyConflictException`. Registered `SetYearlyBaseline` in `Program.cs`.
- Task 3: Added `GET /households/{id}` and `PUT /households/{id}/yearly-baseline` to `HouseholdEndpoints.cs`. Beyond the story's literal text, added an explicit authorization check (`TryAuthorizeHousehold`) comparing the route `{id}` against `ICurrentHouseholdAccessor.HouseholdId` and returning 403 on mismatch/no-household — required because `Household` carries no AD-3 query filter on itself, so without this check any authenticated principal could read or overwrite another Household's Yearly Baseline by guessing its id (IDOR). Covered by a dedicated test (`A_principal_cannot_read_or_edit_another_Households_Yearly_Baseline`).
- Task 4: Added `YearlyBaselineForm` in its own `web/src/components/yearly-baseline/` folder; threaded `householdId` from `App.tsx`'s `ready` session state through `SettingsPage` (which previously took no household prop) so the form can call `GET /households/{id}`. Wired in as a sibling of `TaggingScaffoldManager` in `SettingsPage`.
- Task 5: Added `yearlyBaseline.*` key blocks to both `en-US` and `de-DE` `translation.json`.
- Task 6: Added `SetYearlyBaselineTests.cs` (Application), `YearlyBaselineEndpointsTests.cs` (API, Testcontainers-backed Postgres, includes the two-writer 409 race and the cross-Household 403 test), and `yearly-baseline-form.test.tsx` (Vitest/Testing Library). Also updated the pre-existing `App.test.tsx` Settings-navigation test, which needed a new mocked `GET /api/households/{id}` route now that `SettingsPage` renders `YearlyBaselineForm`.
- Full regression: `dotnet test EnergyTracker.sln --configuration Release` → 132/132 passed (Application, Infrastructure, Architecture, Api.Tests incl. Testcontainers Postgres). `npx vitest run` → 34/34 passed. `npx tsc -b` clean. `npx oxlint` clean (one pre-existing unrelated warning in `button.tsx`). `npm run build` succeeds.

### File List

- `src/EnergyTracker.Domain/Household.cs`
- `src/EnergyTracker.Infrastructure/Configurations/HouseholdConfiguration.cs`
- `src/EnergyTracker.Infrastructure.Migrations.Postgres/Migrations/20260815140112_AddYearlyBaselineAndVersionToHousehold.cs`
- `src/EnergyTracker.Infrastructure.Migrations.Postgres/Migrations/20260815140112_AddYearlyBaselineAndVersionToHousehold.Designer.cs`
- `src/EnergyTracker.Infrastructure.Migrations.Postgres/Migrations/EnergyTrackerDbContextModelSnapshot.cs`
- `src/EnergyTracker.Infrastructure.Migrations.SqlServer/Migrations/20260815140116_AddYearlyBaselineAndVersionToHousehold.cs`
- `src/EnergyTracker.Infrastructure.Migrations.SqlServer/Migrations/20260815140116_AddYearlyBaselineAndVersionToHousehold.Designer.cs`
- `src/EnergyTracker.Infrastructure.Migrations.SqlServer/Migrations/EnergyTrackerDbContextModelSnapshot.cs`
- `src/EnergyTracker.Application/SetYearlyBaseline.cs` (new)
- `src/EnergyTracker.Application/HouseholdConcurrencyConflictException.cs` (new)
- `src/EnergyTracker.Application/Ports/IHouseholdRepository.cs`
- `src/EnergyTracker.Infrastructure/Adapters/HouseholdRepository.cs`
- `src/EnergyTracker.Api/Program.cs`
- `src/EnergyTracker.Api/Endpoints/HouseholdEndpoints.cs`
- `web/src/components/yearly-baseline/yearly-baseline-form.tsx` (new)
- `web/src/components/yearly-baseline/yearly-baseline-form.test.tsx` (new)
- `web/src/components/settings/settings-page.tsx`
- `web/src/App.tsx`
- `web/src/App.test.tsx`
- `web/src/locales/en-US/translation.json`
- `web/src/locales/de-DE/translation.json`
- `tests/EnergyTracker.Application.Tests/SetYearlyBaselineTests.cs` (new)
- `tests/EnergyTracker.Api.Tests/YearlyBaselineEndpointsTests.cs` (new)

## Change Log

- 2026-08-15: Implemented Yearly Baseline configuration end-to-end (domain/EF/migration, `SetYearlyBaseline` use case, `GET`/`PUT` household endpoints with Household-ownership authorization, frontend form with presets and 409 conflict handling, i18n, tests). Full regression green (132 .NET tests, 34 frontend tests).

### Review Findings

- [x] [Review][Patch] Cross-provider decimal precision mismatch on `YearlyBaselineKwh` — Postgres migration generates `numeric` (unbounded), SQL Server generates `decimal(18,2)`, so the same input value is stored with different precision depending on provider even though `HouseholdConfiguration.cs`'s adjacent comment claims "no provider-specific column mapping" (AD-2). Fix: add an explicit `.HasPrecision(18, 2)` (or similar) so both providers agree. [src/EnergyTracker.Infrastructure/Configurations/HouseholdConfiguration.cs:28]
- [x] [Review][Patch] No upper bound on `YearlyBaselineKwh` — `SetYearlyBaseline.ExecuteAsync` only rejects values `<= 0`; an oversized value can overflow SQL Server's `decimal(18,2)` column, and `HouseholdRepository.UpdateYearlyBaselineAsync` only catches `DbUpdateConcurrencyException`, so the overflow surfaces as an unhandled 500 instead of a `ProblemDetails` 400. The frontend input also has no `max`, and an extreme value can parse to `Infinity`, which `JSON.stringify` turns into `null`. Fix: add a sane upper-bound validation in `SetYearlyBaseline` (and mirror it client-side). [src/EnergyTracker.Application/SetYearlyBaseline.cs:15]
- [x] [Review][Patch] Silent 409-refetch failure contradicts the message shown to the user — on conflict, `yearly-baseline-form.tsx` tries to refetch the current value but swallows a failed refetch in an empty `catch`, while unconditionally showing "We've loaded the latest value below" even when that refetch failed and the stale value is still displayed. Fix: only show that specific copy when the refetch actually succeeds; show a different message (e.g. "please reload") when it doesn't. [web/src/components/yearly-baseline/yearly-baseline-form.tsx:120-130]
- [x] [Review][Patch] Input and preset buttons stay enabled while a submit is in flight — a late-arriving `PUT` response's `.then` overwrites `input`/`version` state, so a user who edits the field or clicks a different preset during the request's round trip can have that newer, unsaved edit silently clobbered. Fix: disable the input and preset buttons while `submitting` is true (mirror the existing `disabled={submitting || !input}` on the submit button). [web/src/components/yearly-baseline/yearly-baseline-form.tsx:170-178]
- [x] [Review][Patch] No retry affordance on initial load failure — the `loadError` branch renders a static, dead-end message; a single transient failure on the mount-time `GET /households/{id}` permanently blocks the form until the whole Settings page is reloaded. Fix: add a retry button that re-runs the fetch. [web/src/components/yearly-baseline/yearly-baseline-form.tsx:145-147]
- [x] [Review][Patch] Preset kWh values are triplicated with nothing enforcing sync — `1500/2500/3500/4250` live in the `PRESETS` array in `yearly-baseline-form.tsx` and are separately hand-typed into the human-readable preset strings in both `en-US` and `de-DE` `translation.json`. A future change to one without the others produces a mislabeled button. Fix: interpolate the number into the translation (e.g. `t('yearlyBaseline.preset1', { kwh: preset.kwh })` with a `{{kwh}}` placeholder) so `PRESETS` is the single source of truth. [web/src/components/yearly-baseline/yearly-baseline-form.tsx:42-47]
- [x] [Review][Patch] `HouseholdDetailsResponse` duplicates `HouseholdResponse` — a near-identical second response record (`Id`, `Locale`, `Currency` repeated, plus the two new fields) was introduced instead of extending the existing one, so two shapes for "a Household" now exist that can independently drift as later stories add fields. Fix: consolidate into one response record (adding fields is non-breaking for existing consumers). [src/EnergyTracker.Api/Endpoints/HouseholdEndpoints.cs:133,135]
- [x] [Review][Patch] Misleading test comment — `PUT_yearly_baseline_with_a_stale_version_returns_409_on_the_second_writer` is described as "a genuine two-writer race" but performs two fully sequential, awaited `PUT`s (no `Task.WhenAll`/real interleaving); it correctly tests stale-version rejection, not concurrent contention. Fix: reword the comment to describe what's actually tested. [tests/EnergyTracker.Api.Tests/YearlyBaselineEndpointsTests.cs:65-66]
- [x] [Review][Defer] Unhandled not-found path on `Household` lookups — `GET /households/{id}` and `HouseholdRepository.UpdateYearlyBaselineAsync` both use `SingleAsync` with no not-found guard; a missing row throws an uncaught `InvalidOperationException` → 500 instead of a `ProblemDetails` 404. Currently unreachable (no Household-deletion feature exists) and mirrors the pre-existing `AcceptInviteAsync` `SingleAsync` pattern already in the codebase — deferred, pre-existing. [src/EnergyTracker.Api/Endpoints/HouseholdEndpoints.cs:90], [src/EnergyTracker.Infrastructure/Adapters/HouseholdRepository.cs:96]
