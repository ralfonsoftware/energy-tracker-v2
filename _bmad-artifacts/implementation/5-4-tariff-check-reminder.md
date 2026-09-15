---
baseline_commit: 62b9a72
---

# Story 5.4: Tariff Check Reminder

Status: ready-for-dev

<!-- Note: Validation is optional. Run validate-create-story for quality check before dev-story. -->

## Story

As a Household member,
I want a proactive prompt to revisit my Tariff comparison at a sensible time,
so that I don't have to remember to check manually.

## Scope Reality Check — Read This First

This is the **fourth and final story in Epic 5**, and the first to touch `Household` (Stories 5.1–5.3 only touched `Tariff`). It builds the `tariff-check-card` banner that `mockups/key-tariff-radar.html` and `mockups/key-dashboard.html` both render but that no prior story built — **`dashboard-page.tsx`'s own header comment currently says so explicitly**: "Deliberately does NOT render a Tariff Check prompt card — its due-date gating (FR-15) is Epic 5, not built yet." That comment is now stale; this story replaces it.

- **This story owns:** the due-ness computation (FR-15, AD-7 — pure, synchronous, no persisted schedule), the `GET /api/tariff-check` singleton endpoint, and the `TariffCheckCard` component rendered on **both** Dashboard and Tariff Radar (per both mockups — this is not Dashboard-only).
- **Two real ambiguities the epic/PRD left unresolved were resolved during story creation (not left for you to guess) — see Dev Notes:**
  1. **What "recurring cadence" computes**, given AD-7 means this feature has **zero persisted state** (no "last checked"/"last dismissed" timestamp anywhere — unlike Status, which persists `StatusSnapshot`). Resolved with Ralf: **monotonic** — once the gate opens, `IsDue` stays `true` forever after (matches AC #4's literal "stays open" wording, and both mockups only ever show two states: gate-closed, or gate-open-and-overdue — never a third "quietly closed again" state). `Household.TariffCheckCadenceMonths` is still added as a real column (default 3) so AC #2's "editable per household" is structurally true, but **it is not read anywhere in this story's `IsDue` computation** — see Dev Notes for why that's the deliberate, discussed choice, not an oversight.
  2. **Whether the card renders at all when no Tariff is configured.** AC #3 says "no reminder fires," but doesn't say whether that means "hide the card" or "show neutral copy about a check that doesn't exist yet." Resolved by direct mockup evidence: `key-dashboard.html`'s two first-run empty-state frames (no Status yet) render **no** `.tariff-quiet` line at all — only the two populated frames do. This story renders `TariffCheckCard` only when `GET /api/tariff-check` returns a non-null body (a current Tariff exists); when it returns null, nothing renders — not even neutral "nothing due" copy.
- **Do NOT build a Settings UI for editing `TariffCheckCadenceMonths`.** `TrendingThresholdKwh` and `LowConfidenceGapDays` (Story 2.4) are the direct precedent: both are Household-scoped config columns with sane defaults, and **neither has any settings UI today** (verified: zero frontend references to either field). `TariffCheckCadenceMonths` follows the identical pattern — a column with a default, no UI, "editable per household" satisfied structurally (a future story can add the UI when/if the monotonic-vs-cadence question is revisited). `EXPERIENCE.md`'s Settings IA row listing "Tariff Check cadence" is aspirational placement, not a requirement of this story.
- **Do NOT add any dismiss/"mark as checked" mechanism.** Nothing in FR-15's ACs mentions one, and AD-7 explicitly rules out any persisted per-household reminder state beyond the Tariff's own `ContractStartDate`/`ContractPeriodMonths`.
- **No new persisted schedule, no background job, no notification.** This is a `GET`-time computation exactly like `GetCurrentStatus` — see AD-7's own text: "Current Status... and Tariff Check Reminder due-ness (FR-15) are pure, synchronous computations evaluated on every relevant read — never precomputed by a background schedule."

## Acceptance Criteria

(Sourced from `_bmad-artifacts/planning/epics/epic-5-tariff-savings-radar.md`, Story 5.4 block, lines 98-132.)

1. **Given** the current Contract Period, **when** more than 3 months remain before it ends, **then** no reminder fires (FR-15).
2. **Given** the 3-month gate has opened, **when** no custom cadence is set, **then** the default recurring cadence is every 3 months, editable per household (FR-15).
3. **Given** no Tariff is configured yet, **when** the reminder logic evaluates, **then** no reminder fires — there's nothing to compare against (FR-15).
4. **Given** the Contract Period represents a minimum term, not necessarily a hard end date, **when** the minimum term elapses, **then** the reminder gate opens and stays open on the recurring cadence whether the tariff then ends outright or auto-continues on a rolling basis — no explicit contract end date is required (FR-15).
5. **Given** the contract start date or Contract Period is edited after the reminder schedule was computed, **when** saved, **then** the gate and cadence recompute against the new dates going forward (FR-15).
6. **Given** the reminder's due-ness, **when** evaluated, **then** it's a pure synchronous computation evaluated on every relevant read, never precomputed by a background schedule (AD-7).
7. **Given** the Tariff Check prompt card, when a check is due, **when** rendered, **then** it appears at deliberately lower visual weight than the Status card; when nothing is due, it shows neutral "nothing due right now" microcopy at the same quiet weight, never a fabricated recommendation (UX-DR5).

**Not explicitly written above but required for the system to work end-to-end (project-context.md's "the dev agent owns this" rule) — see Dev Notes' "Edge cases beyond the stated ACs":**
- AC #2's "editable per household" is satisfied structurally (a real `Household` column with a default), not behaviorally — this story's `IsDue` computation does not branch on the cadence value at all. This is the resolved-ambiguity #1 above, not an accidental gap.
- The card's copy must never claim a fabricated fact like "it's been 3 months since you last compared rates" — this codebase has **no** "last compared" timestamp anywhere (AD-7 forbids persisting one for this feature). The mockup's literal due-state copy makes exactly this claim; this story's copy must not.
- Whether the card renders at all with no Tariff configured (resolved-ambiguity #2 above: it doesn't).

## Tasks / Subtasks

- [ ] **Task 1: Add `Household.TariffCheckCadenceMonths`** (AC: #2)
  - [ ] `src/EnergyTracker.Domain/Household.cs`: add `public int TariffCheckCadenceMonths { get; set; } = 3;` immediately after `LowConfidenceGapDays` — same shape, same "Household-scoped config, never a code literal" reasoning as its two Story 2.4 siblings (AD-15). Doc-comment it clearly as **not currently read by any due-ness computation** (see Task 2) so a future reader doesn't assume it does something today.
  - [ ] `src/EnergyTracker.Infrastructure/Configurations/HouseholdConfiguration.cs`: add `builder.Property(h => h.TariffCheckCadenceMonths).HasDefaultValue(3);` right after the existing `LowConfidenceGapDays` config — identical shape (no `.HasPrecision`, it's an `int` like `LowConfidenceGapDays`, not `decimal` like `TrendingThresholdKwh`).
  - [ ] Run `scripts/add-migration.sh AddTariffCheckCadenceMonthsToHousehold` (never `dotnet ef migrations add` directly — AD-2) to generate both provider migrations. Verify the generated migration is a single `AddColumn<int>` with `defaultValue: 3`, mirroring the existing `LowConfidenceGapDays` migration (`20260817051304_AddStatusSnapshotAndHouseholdThresholds.cs` is the exact reference shape).

- [ ] **Task 2: `GetTariffCheckReminder` use case — monotonic gate computation** (AC: #1, #3, #4, #5, #6)
  - [ ] New file `src/EnergyTracker.Application/GetTariffCheckReminder.cs`:
    ```csharp
    public record TariffCheckReminderResult(bool IsDue, DateTimeOffset GateOpensAtUtc);

    /// <summary>Computes whether the caller's Household is due for a Tariff Check live, synchronously, at request time — undefined (null) with no current Tariff configured (AC #3; AD-7, FR-15).</summary>
    public class GetTariffCheckReminder(ITariffRepository tariffRepository)
    {
        public async Task<TariffCheckReminderResult?> ExecuteAsync(Guid householdId, CancellationToken cancellationToken)
        {
            var currentTariff = await tariffRepository.FindCurrentForHouseholdAsync(householdId, cancellationToken);
            if (currentTariff is null)
            {
                return null;
            }

            // AC #4: ContractPeriodMonths is a MINIMUM term, not a hard end date — this is computed
            // fresh from the current Tariff's own two fields on every call, never a stored/cached
            // schedule, so it needs no concept of an actual contract end, auto-renewal, or rolling
            // continuation to "stay open." AC #5 (recompute on edit) falls out of this for free:
            // EditTariff writes new values onto this same row, and this method always re-reads them
            // live — there is no separate schedule that could go stale.
            var minimumTermEndUtc = currentTariff.ContractStartDate.AddMonths(currentTariff.ContractPeriodMonths);
            var gateOpensAtUtc = minimumTermEndUtc.AddMonths(-3);

            // AC #1/#4, resolved ambiguity #1 (Dev Notes): monotonic, not cyclic. Once true, stays
            // true — TariffCheckCadenceMonths is deliberately NOT read here.
            var isDue = DateTimeOffset.UtcNow >= gateOpensAtUtc;

            return new TariffCheckReminderResult(IsDue: isDue, GateOpensAtUtc: gateOpensAtUtc);
        }
    }
    ```
  - [ ] No new repository method needed — `ITariffRepository.FindCurrentForHouseholdAsync` (Story 5.2's own addition) is exactly "the Household's currently-effective Tariff," already used by `CompareTariff`.
  - [ ] Register in `src/EnergyTracker.Api/Program.cs`: `builder.Services.AddScoped<GetTariffCheckReminder>();` next to the other Tariff use-case registrations (after `CompareTariff`, line ~339).

- [ ] **Task 3: `GET /api/tariff-check` endpoint** (AC: #1, #3, #4, #5, #6)
  - [ ] `src/EnergyTracker.Api/Endpoints/TariffEndpoints.cs`: add inside `MapTariffEndpoints`, after the `/tariffs/compare` block:
    ```csharp
    // Singleton resource, not a collection (consistent with /api/status, /api/session — Consistency
    // Conventions). 200 with a null body when no current Tariff exists (AC #3) — same "is there
    // one?" shape as GET /api/status and POST /api/tariffs/compare.
    api.MapGet("/tariff-check", async (
        ICurrentHouseholdAccessor householdAccessor,
        GetTariffCheckReminder getTariffCheckReminder,
        CancellationToken cancellationToken) =>
    {
        if (!TryGetHouseholdId(householdAccessor, out var householdId, out var forbidden))
        {
            return forbidden;
        }

        var result = await getTariffCheckReminder.ExecuteAsync(householdId, cancellationToken);
        return Results.Ok(result is null ? null : ToTariffCheckResponse(result));
    });
    ```
  - [ ] Add `private static TariffCheckResponse ToTariffCheckResponse(TariffCheckReminderResult result) => new(result.IsDue, result.GateOpensAtUtc);` and `public record TariffCheckResponse(bool IsDue, DateTimeOffset GateOpensAtUtc);` at the bottom of the file, next to the other response records.

- [ ] **Task 4: Frontend — `tariff-check-api.ts`** (AC: #1, #3, #4, #5, #6)
  - [ ] New file `web/src/lib/tariff-check-api.ts`, mirroring `status-api.ts`'s exact shape (same `ApiError`/`toApiError`, same "empty body → null, not `JSON.parse('')`" precedent):
    ```typescript
    export interface TariffCheckReminderDto {
      isDue: boolean
      gateOpensAtUtc: string
    }

    export async function fetchTariffCheckReminder(): Promise<TariffCheckReminderDto | null> {
      const response = await fetch('/api/tariff-check', { credentials: 'include' })
      if (!response.ok) {
        throw await toApiError(response)
      }
      const text = await response.text()
      return text ? (JSON.parse(text) as TariffCheckReminderDto) : null
    }
    ```
    Duplicate the small `ApiError`/`toApiError` pair locally exactly as `status-api.ts` already does (that file's own comment notes it mirrors `meter-regression-api.ts`'s copy — this is the established per-file convention in this codebase, not something to suddenly extract into a shared module).

- [ ] **Task 5: Frontend — `TariffCheckCard` component** (AC: #7)
  - [ ] New file `web/src/components/tariff/tariff-check-card.tsx`. Props: `{ reminder: TariffCheckReminderDto | null; locale: string; onClick?: () => void }`. **Returns `null` (renders nothing) when `reminder` is null** — resolved ambiguity #2 (Scope Reality Check). Otherwise renders a single quiet block:
    - Not a `GlassCard` — components.md is explicit this surface has **no glass-blur, no glow, no border emphasis**, deliberately lower-weight than every other card in the product. Plain `div` with the new tokens from Task 6.
    - Due (`reminder.isDue === true`): `t('tariffCheck.due')` — plain-language, **does not claim any "it's been N months since you checked" fact** (no such data exists — see Scope Reality Check). Something like "Tariff check — worth a look." plus a second sentence naming only what's actually known (the minimum contract term has ended or is ending soon) — do not invent a more specific claim than that.
    - Not due (`reminder.isDue === false`): `t('tariffCheck.notDue', { date: dateFormat.format(new Date(reminder.gateOpensAtUtc)) })` — "Tariff check — nothing due right now. Next check around {{date}}." `dateFormat` is `new Intl.NumberFormat`... no — `new Intl.DateTimeFormat(locale, { dateStyle: 'medium' })`, the exact same construction `tariff-history-list.tsx` (line 70) already uses; don't invent a different date-formatting approach.
    - `onClick` (when provided) makes the whole block a `<button type="button">` (tap target, per `dashboard-page.tsx`'s existing icon-button precedent) navigating to Tariff Radar; when omitted (Tariff Radar's own rendering of this card — already on the page, nothing to navigate to), render a plain non-interactive `<div>`.
  - [ ] Verdict-word framing only, no numbers/amounts here (that's Story 5.3's signal card, a different component on a different part of the page) — this card is purely the quiet FR-15 prompt.

- [ ] **Task 6: Color tokens for the quiet card** (AC: #7)
  - [ ] **Before inventing a new text token, check whether the existing `text-muted-foreground` clears AA against the new background** (see below) — this codebase's accessibility review (`review-accessibility.md`) explicitly flagged the mockup's own bespoke `text-quiet`/`tariff-quiet` low-alpha tokens as failing AA systemically (light ≈2.1–2.5:1, dark ≈2.7–2.9:1 — well under 4.5:1), calling out **this exact card's "nothing due" line by name** as one of the real functional-copy failures. `text-muted-foreground` is a plain shadcn default already used for secondary copy elsewhere in this codebase (`status-detail-dialog.tsx` labels, `dashboard-page.tsx`'s detail-trigger link) — reusing it sidesteps reproducing a documented, named AA failure instead of copying the mockup's broken value verbatim. **Actually verify** (a contrast-ratio calculation against the real composited background from the next bullet, not eyeballing) — only add a dedicated new text token if `text-muted-foreground` fails to clear 4.5:1 there.
  - [ ] `web/src/index.css`: add near-transparent surface tokens (components.md: "no glass-blur, no glow, no border emphasis"), structurally mirroring the existing `--color-status-*`/`--color-attractiveness-*` pattern (`@theme inline` mapping + `:root`/`.dark` raw values):
    ```css
    --color-tariff-check-card-bg: var(--tariff-check-card-bg);
    --color-tariff-check-card-border: var(--tariff-check-card-border);
    ```
    Dark-mode values are verbatim from `mockups/key-dashboard.html` line 191 (`.theme.dark .tariff-quiet`): `background: rgba(220,245,230,0.03); border: rgba(210,235,220,0.05)`. Light-mode values are verbatim from the same file's line 226 (`.theme.light .tariff-quiet`): `background: rgba(255,255,255,0.4); border: rgba(40,70,50,0.08)` — **unlike Story 5.3's attractiveness tokens, this mockup file (`key-dashboard.html`, unlike `key-tariff-radar.html`) already renders both themes**, so these are directly reusable, not independently derived.
  - [ ] Do not reuse `--surface-glass`/`--surface-panel-back` (those carry the blur/glow treatment components.md says this card must not have).

- [ ] **Task 7: Wire into Dashboard and Tariff Radar** (AC: #7)
  - [ ] `web/src/App.tsx`: add `tariffCheck` state (`TariffCheckReminderDto | null`) + `refreshTariffCheck` callback, mirroring `status`/`refreshStatus` exactly (try/fetch/setState, catch → `setTariffCheck(null)`, no separate loading flag needed — `TariffCheckCard` already renders nothing for `null`, whether that means "loading" or "no Tariff," so no extra skeleton state is needed here, unlike `StatusCard`). Call `refreshTariffCheck()` in the same `useEffect` that already calls `refreshStatus()`/`refreshOpenRegressionPrompt()` on `state.status === 'ready'` (~line 183). **Do not** wire it into `registerOfflineSync`'s callback (Tariff edits are a foreground-only surface, unlike Meter Readings — there's no offline Tariff queue to sync).
  - [ ] Pass `tariffCheck={tariffCheck}` to `<DashboardPage>`. Pass `tariffCheck={tariffCheck}` and `onTariffCheckChanged={refreshTariffCheck}` to `<TariffRadarPage>`.
  - [ ] `web/src/components/dashboard/dashboard-page.tsx`: add `tariffCheck: TariffCheckReminderDto | null` prop. Render `<TariffCheckCard reminder={tariffCheck} locale={household.locale} onClick={onTariffRadarClick} />` between `<StatusCard>` and the `{showPopulated && ...}` primary-button row — matches both mockups' vertical order (Status card → tariff-quiet line → primary button). **Update the file's own header comment** (currently: "Deliberately does NOT render a Tariff Check prompt card — its due-date gating (FR-15) is Epic 5, not built yet") — that statement is now false; remove or correct it.
  - [ ] `web/src/components/tariff/tariff-radar-page.tsx`: add `tariffCheck: TariffCheckReminderDto | null` and `onTariffCheckChanged: () => void` props. Render `<TariffCheckCard reminder={tariffCheck} locale={locale} />` (no `onClick` — already on this page) directly below the `<h1>` page title, above `<TariffConfigurationForm>` — matches `key-tariff-radar.html`'s layout (`.tariff-check-card` immediately under `.page-title`). Call `onTariffCheckChanged()` from `TariffConfigurationForm`'s existing `onCreated` handler, alongside the existing `setRefreshNonce((n) => n + 1)` (a newly-created Tariff can immediately change due-ness).
  - [ ] `web/src/components/tariff/tariff-history-list.tsx`: add a new `onTariffMutated: () => void` prop, called from `EditTariffDialog`'s `onSaved` alongside the existing `setEditing(null); load(page)` (editing `ContractStartDate`/`ContractPeriodMonths` on the current Tariff — AC #5 — must refresh the reminder too). Thread `onTariffMutated={onTariffCheckChanged}` through from `TariffRadarPage`.

- [ ] **Task 8: i18n — both locales** (AC: #7)
  - [ ] Add a top-level `tariffCheck` namespace (sibling to the existing `tariff` namespace, not nested under it — this card appears on Dashboard too, outside any Tariff-specific view) to both `web/src/locales/en-US/translation.json` and `de-DE/translation.json`: `due` (two short sentences, no fabricated "since you last checked" claim), `notDue` (with a `{{date}}` interpolation — "Tariff check — nothing due right now. Next check around {{date}}.").

- [ ] **Task 9: Tests** (AC: all)
  - [ ] `tests/EnergyTracker.Application.Tests/GetTariffCheckReminderTests.cs` (new, mirrors `CompareTariffTests.cs`'s NSubstitute-against-`ITariffRepository` style): no current Tariff → `null` (AC #3); >3 months remain before `ContractStartDate.AddMonths(ContractPeriodMonths)` → `IsDue == false` (AC #1); exactly at the 3-month boundary → `IsDue == true` (gate is inclusive — "no earlier than 3 months before" means the boundary instant itself is already due); minimum term already elapsed well in the past → `IsDue == true` (AC #4, "stays open"); `GateOpensAtUtc` equals `ContractStartDate.AddMonths(ContractPeriodMonths).AddMonths(-3)` exactly, asserted directly (not just via `IsDue`); two otherwise-identical calls differing only in `ContractStartDate`/`ContractPeriodMonths` produce different `IsDue`/`GateOpensAtUtc` (documents AC #5 — there is no separate schedule to go stale, so this is the whole test for that AC).
  - [ ] `tests/EnergyTracker.Api.Tests/TariffEndpointsTests.cs`: extend with `GET /api/tariff-check` cases mirroring the existing `POST /api/tariffs/compare` 200-with-null-body and 200-with-computed-result tests (`TariffEndpointsTests.cs` lines ~322-337 area) plus the standard 403-no-household case every other endpoint in this file already covers.
  - [ ] `web/src/lib/tariff-check-api.test.ts` (new, mirrors `status-api.test.ts`): success, empty-body → `null`, non-ok → thrown `ApiError`.
  - [ ] `web/src/components/tariff/tariff-check-card.test.tsx` (new): `reminder === null` renders nothing; due state renders the due copy (and no fabricated "since you last checked" text); not-due state renders the formatted `gateOpensAtUtc` date; `onClick` fires on tap when provided; renders as a non-interactive block when `onClick` is omitted.
  - [ ] `web/src/components/dashboard/dashboard-page.test.tsx`: extend to assert `TariffCheckCard` renders/doesn't render per the `tariffCheck` prop, positioned after the Status card.
  - [ ] `web/src/components/tariff/tariff-history-list.test.tsx`: extend the existing edit-save test to assert `onTariffMutated` fires alongside the existing reload.
  - [ ] `web/src/App.test.tsx`: **every existing `mockFetchRoutes([...])` array that already includes a `GET /api/status` route reaches the dashboard-rendering `'ready'` state and will now also attempt `GET /api/tariff-check`** — add `{ method: 'GET', url: '/api/tariff-check', respond: () => jsonResponse(null) }` to each one (a safe default that renders no card, won't break existing assertions). Grep for `/api/status` in this file first to find every call site needing the addition — there are multiple (the file has 14 separate `mockFetchRoutes([...])` arrays; not all reach `'ready'`, only the ones with `/api/status` do). Add one new test asserting the Dashboard's `TariffCheckCard` renders when `/api/tariff-check` returns a due reminder.
  - [ ] Full backend suite green, full frontend suite green, `dotnet build`/`tsc -b`/`oxlint`/`vite build` all clean before moving to review — this project's established verification bar.

## Dev Notes

- **Resolved ambiguity #1 — the cadence model (confirmed with Ralf during story creation):** FR-15/AD-7 together mean this feature has **zero persisted state** — no `TariffCheckReminderSnapshot`, no "last shown"/"last dismissed" timestamp anywhere, unlike Status (which does persist `StatusSnapshot` on every recompute). Given that, a genuinely *cyclic* "recurring cadence" (due for the first month of every N-month window, quiet the rest) would need either persisted state or an artificial modulo-on-calendar-months rule invented from nothing — and the epic's own edge-case review (`prd/review-edge-case-hunter.md`, "Cadence changed after a reminder is already scheduled") flags this exact question as unresolved by the PRD. Resolved: **monotonic**. Once `IsDue` becomes `true` (the 3-month-before-minimum-term-end gate opens), it stays `true` forever — there is no mechanism in this story that would ever turn it back off, and none of FR-15's ACs describe one (no dismiss/"mark reviewed" action exists). `TariffCheckCadenceMonths` is added to `Household` now (AC #2's literal "editable per household" text) but is inert — not read by `GetTariffCheckReminder` — a deliberate scope boundary, not an oversight. **Do not** try to make it do something clever (e.g. a modulo window) without checking with Ralf first — that was the explicitly-declined alternative.
- **Resolved ambiguity #2 — no Tariff configured yet:** confirmed via direct mockup evidence, not guessed. `mockups/key-dashboard.html`'s two first-run/empty-state frames (no computable Status) render no `.tariff-quiet` line at all; only the two populated frames do. `TariffCheckCard` therefore renders nothing (not neutral "nothing due" copy) when `GET /api/tariff-check` returns a null body.
- **Why `GetTariffCheckReminder` only depends on `ITariffRepository`, not `IHouseholdRepository`:** the monotonic computation needs only the current Tariff's own `ContractStartDate`/`ContractPeriodMonths` — it never reads `TariffCheckCadenceMonths` (see resolved ambiguity #1), so there's no reason to fetch `Household` at all. Don't add an unused dependency "just in case."
- **Why the due-state copy can't reuse the mockup's literal text:** `key-tariff-radar.html`'s due frame reads "It's been 3 months since you last compared rates, and your minimum contract term ended a while back." The first half of that sentence claims knowledge (a tracked "last compared" timestamp) this product does not and — per AD-7 — must not persist. Copying it verbatim would be exactly the "fabricated recommendation" UX-DR5 explicitly forbids. Write copy that only asserts what `GateOpensAtUtc <= now` actually tells you (the minimum contract term is ending soon or has already ended) — see Task 5.
- **AC #1's "3 months" boundary is inclusive, not exclusive:** the epic's own phrasing is "no earlier than 3 months before... ends" — so the reminder is allowed to fire starting exactly at the 3-month mark, not only strictly after it. `IsDue = now >= gateOpensAtUtc` (not `>`) reflects this; the boundary test in Task 9 pins it down.
- **Closest existing analogs to build from:**
  - `src/EnergyTracker.Application/GetCurrentStatus.cs` — the AD-7 "pure computation at request time" shape this use case mirrors structurally (though `GetCurrentStatus` also writes `StatusSnapshot` via a separate seam that this feature deliberately has no equivalent of).
  - `src/EnergyTracker.Application/CompareTariff.cs` — the most recent precedent for a Tariff-domain use case depending on `ITariffRepository.FindCurrentForHouseholdAsync` and returning a nullable result record when no current Tariff exists.
  - `src/EnergyTracker.Api/Endpoints/StatusEndpoints.cs`'s `/status` route — the exact "200, null body when undefined" singleton-resource shape this story's `/tariff-check` route copies.
  - `web/src/lib/status-api.ts` — the exact client-fetch shape (`ApiError`, empty-body-means-null) `tariff-check-api.ts` copies.
  - `web/src/App.tsx`'s `status`/`refreshStatus` — the state-lifted-to-App, fetched-on-ready, passed-down-as-props pattern `tariffCheck`/`refreshTariffCheck` mirrors.
  - `web/src/components/dashboard/status-card.tsx` — for the "quiet, plain-language, never-color-alone" verdict-word discipline generally, though this card is deliberately much simpler (two text states, no color-coded verdict at all — components.md never mentions one for this card, unlike the Status triad or the attractiveness pair).

- **Edge cases beyond the stated ACs (project-context.md's "the dev agent owns this" rule):**
  - A Tariff entered with a **future** `ContractStartDate` (Story 5.1 explicitly allows this — "history is retained... covers until the next one's start date"): `FindCurrentForHouseholdAsync` already filters to `ContractStartDate <= now`, so a future-dated-only Tariff correctly behaves identically to "no current Tariff" (AC #3) — no special-casing needed here, it's inherited for free from the existing repository method.
  - `ContractPeriodMonths.AddMonths` day-of-month rollover (e.g. a 31st-of-the-month start plus an odd period length landing on a shorter month) uses .NET's standard `DateTimeOffset.AddMonths` clamping behavior — same as every other date-math call in this codebase; no bespoke handling needed.
  - A very short `ContractPeriodMonths` (e.g. 1 or 2, shorter than the fixed 3-month pre-end window) means `gateOpensAtUtc` can fall **before** `ContractStartDate` itself — i.e., the gate is already open the moment the Tariff is created. This is correct, not a bug: `TariffValidation` doesn't impose a minimum `ContractPeriodMonths`, and there's no principled reason to special-case it.

### Previous Story Intelligence (Story 5.3)

- 5.3 shipped the two-way attractiveness signal on `tariff-comparison-form.tsx`; this story doesn't touch that file at all — it's a fully separate surface (the quiet FR-15 prompt vs. the signal card is a different component in a different visual position).
- 5.3's own Task 3 process (derive light-mode color values when the mockup is dark-only, verify contrast with an actual calculation rather than eyeballing) is the precedent Task 6 above follows — except this story is lucky: `key-dashboard.html` (unlike `key-tariff-radar.html`, which is dark-only) already renders the tariff-quiet card in **both** themes, so no independent derivation is needed, only reuse + verification of whether `text-muted-foreground` (rather than a new bespoke token) clears AA against those composited backgrounds.
- 5.3's Review Findings included one relevant lesson: a verdict/tie-break computed off an un-rounded raw decimal disagreed visually with a rounded displayed figure. Not directly applicable here (this story has no money figures or verdict rounding), but the general discipline — the server-computed boolean and whatever text the frontend shows for it must never be able to visually disagree — is worth keeping in mind if the due/not-due boundary ever needs display-side rounding of a date. It doesn't currently (dates aren't rounded), so no action needed, just noted.

### Git Intelligence

- Recent commits (`62b9a72` "feat: story 5.3 two-way attractiveness signal (#FR-13) (#55)", `d1f327a` "doc: story 5.3 planning") confirm the established cadence: one planning-doc commit, one squash-merged implementation PR, `feat:`/`fix:`/`doc:` Conventional-Commits prefixes. Branch fresh from `main` (current branch) for this story's implementation.
- This is the epic's last story — `epic-5-retrospective` is listed as `optional` in `sprint-status.yaml` and would be the natural next step after this story ships, not something to build here.

### Project Structure Notes

- Backend: one new file (`GetTariffCheckReminder.cs`), one new migration (both provider projects via `scripts/add-migration.sh`), extensions to `Household.cs`/`HouseholdConfiguration.cs`/`TariffEndpoints.cs`/`Program.cs`. No new port, no new repository method (reuses `ITariffRepository.FindCurrentForHouseholdAsync`).
- Frontend: two new files (`tariff-check-api.ts`, `tariff-check-card.tsx`), extensions to `App.tsx`, `dashboard-page.tsx`, `tariff-radar-page.tsx`, `tariff-history-list.tsx`, `index.css`, both locale files. No new npm packages.
- This story touches `Household.cs` for the first time since Story 2.4 (2026-08-17) — re-read the whole file before editing (it's short) rather than assuming only the two Story 2.4 fields exist near where you're inserting.

### References

- [Source: _bmad-artifacts/planning/epics/epic-5-tariff-savings-radar.md#Story-5.4] — Story 5.4 definition and ACs (lines 98-132); Epic 5 header (FR-10–FR-15, NFR6/8/9/10, AD-4/5/7/11, UX-DR5/7/10/12) — only FR-15, AD-7, UX-DR5 are new to this story (the rest were satisfied by 5.1–5.3).
- [Source: _bmad-artifacts/planning/epics/requirements-inventory.md] — FR-15 (line 29, full testable text incl. "gated to no earlier than 3 months... then recurring at a customizable cadence"); AD-7 (line 89, "current Status... and Tariff Check Reminder due-ness (FR-15) are pure synchronous computations... never precomputed on a schedule"); UX-DR5 (line 115, the Tariff Check prompt card's full spec).
- [Source: _bmad-artifacts/planning/architecture/architecture-energy-tracker-2026-08-09/ARCHITECTURE-SPINE/invariants-rules.md#AD-7] — full AD-7 rule text and rationale, confirming Tariff Check Reminder due-ness gets no `StatusSnapshot`-equivalent persisted history.
- [Source: _bmad-artifacts/planning/prds/prd-energy-tracker-2026-08-08/prd/4-features.md] — lines 201-208, FR-15's full feature-doc text (the four testable bullets this story's ACs are sourced from).
- [Source: _bmad-artifacts/planning/prds/prd-energy-tracker-2026-08-08/review-edge-case-hunter.md] — lines 83, 86: the two open questions ("Tariff edited mid-cycle" and "Cadence changed after a reminder is already scheduled") that motivated this story's resolved-ambiguity discussion with Ralf.
- [Source: _bmad-artifacts/planning/ux-designs/ux-energy-tracker-2026-08-08/DESIGN/components.md] — line 15-17, "Tariff Check prompt card" component spec (quiet weight, no glass-blur/glow/border-emphasis, shown-only-when-due vs. neutral-microcopy-otherwise).
- [Source: _bmad-artifacts/planning/ux-designs/ux-energy-tracker-2026-08-08/EXPERIENCE.md] — line 28 (Dashboard IA row), line 63 (Tariff Check prompt card Component Pattern row), line 86 ("No Tariff Check currently due" State Pattern row), line 183-184 (Key Flow UJ-2 step 3-4).
- [Source: _bmad-artifacts/planning/ux-designs/ux-energy-tracker-2026-08-08/review-accessibility.md] — line 9, the named, still-open AA gap for this exact card's "nothing due" microcopy (`tariff-quiet` token) that Task 6 must not reproduce.
- [Source: mockups/key-dashboard.html] — lines 144-145 (`.tariff-quiet` CSS), 191 (dark values), 226 (light values — **both themes rendered here**, unlike most other mockup files in this set), 279/349 (populated-frame markup), absence of the line in the empty-state frames (resolved ambiguity #2's evidence).
- [Source: mockups/key-tariff-radar.html] — lines 164-174 (`.tariff-check-card` CSS, dark-only), 272 (due-state markup/copy — **not** reusable verbatim, see Dev Notes), 331 (not-due-state markup/copy — reusable, no fabricated claim).
- [Source: src/EnergyTracker.Domain/Household.cs] — `TrendingThresholdKwh`/`LowConfidenceGapDays`, the direct Story 2.4 precedent for a Household-scoped config column with a default and no settings UI.
- [Source: src/EnergyTracker.Application/GetCurrentStatus.cs] — the AD-7 "pure live computation" pattern this use case mirrors.
- [Source: src/EnergyTracker.Application/CompareTariff.cs] — the most recent `ITariffRepository.FindCurrentForHouseholdAsync`-based use case, and the "return null when no current Tariff" precedent.
- [Source: src/EnergyTracker.Application/Ports/ITariffRepository.cs] — `FindCurrentForHouseholdAsync`'s own doc comment, confirming it's exactly "the Household's currently-effective Tariff."
- [Source: src/EnergyTracker.Api/Endpoints/StatusEndpoints.cs] — the `/status` singleton-endpoint shape (`TryGetHouseholdId`, 200-null-body pattern) this story's `/tariff-check` route copies verbatim.
- [Source: web/src/lib/status-api.ts] — the exact client shape `tariff-check-api.ts` mirrors.
- [Source: web/src/App.tsx] — lines 49-83 (`status`/`refreshStatus`), 163-185 (the `'ready'`-gated fetch effects) this story's `tariffCheck`/`refreshTariffCheck` wiring extends.
- [Source: web/src/components/dashboard/dashboard-page.tsx] — lines 34-40, the now-stale "deliberately does NOT render a Tariff Check prompt card" comment this story corrects; the file this story adds `TariffCheckCard` into.
- [Source: web/src/components/tariff/tariff-radar-page.tsx], [web/src/components/tariff/tariff-history-list.tsx] — the files this story extends for the second `TariffCheckCard` placement and its post-edit refresh wiring.
- [Source: web/src/App.test.tsx] — the `mockFetchRoutes` test-mocking convention; every array already including `/api/status` needs `/api/tariff-check` added too (Task 9).
- [Source: _bmad-artifacts/project-context.md] — project-wide conventions (AD-2 migration script, AD-15 household-scoped config, naming, testing, i18n discipline).

## Change Log

- 2026-09-15: Story created (create-story). Scoped to FR-15/AD-7/UX-DR5 — the last story in Epic 5. Resolved two real ambiguities the PRD/epic left open during drafting, both confirmed with Ralf: (1) given AD-7's "zero persisted state" constraint, "recurring cadence" computes as **monotonic** (once due, stays due forever) rather than a cyclic on/off window — `Household.TariffCheckCadenceMonths` is added as a real column for AC #2's structural "editable per household" requirement but is deliberately not read by the due-ness computation this story ships; (2) the card renders nothing at all (not neutral copy) when no Tariff is configured, confirmed via direct mockup evidence (`key-dashboard.html`'s empty-state frames never render the `.tariff-quiet` line). Also flagged and resolved a copy-accuracy issue found while reading the mockup: its due-state text ("It's been 3 months since you last compared rates...") asserts a "last compared" fact this product has no data for and must not fabricate (AD-7, UX-DR5) — this story's copy must state only what `GateOpensAtUtc` actually proves. Confirmed `dashboard-page.tsx`'s own header comment is now stale ("Deliberately does NOT render a Tariff Check prompt card... not built yet") and must be corrected as part of this story, not left dangling.
