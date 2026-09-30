---
baseline_commit: b147dd8
---

# Story 8.11: Language Toggle — Household Locale Switch

Status: review

<!-- Note: Validation is optional. Run validate-create-story for quality check before dev-story. -->

## Story

As a Household member,
I want to switch the household's language between Deutsch and English from the Profile menu,
so that the app's language and number/date formats match what the household actually wants, after creation as well as at creation.

## Acceptance Criteria

1. **Given** the Profile menu is open (≥660px), **when** rendered, **then** below the Appearance row it shows a "Language" row with the Preference icon strip (UX-DR32) offering `DE` and `EN` (endonyms `Deutsch` / `English` as accessible names — never translated, never flags) and the scope sub-label "Whole household" (de: "Ganzer Haushalt"); the segment matching the persisted `Household.Locale` is selected.
2. **Given** a member selects the other language, **when** the selection is made, **then** the segment shows selected immediately with a saving indicator, the new `Household.Locale` is persisted through a new authenticated Household-scoped endpoint (any Household member may call it — no admin role exists in v1), and on success the UI language **and** number/date formatting switch together without reload (AD-18), the menu stays open, and a polite live region announces the change in the new language.
3. **Given** the session/household response and the i18next instance, **when** the app loads for a signed-in member, **then** the active UI language is `Household.Locale` — the browser-language detector no longer overrides it after Household creation; the detector remains only for pre-Household screens (Household creation display language), preserving Story 1.5's explicit-choice behavior; this also removes the language-vs-date-format mismatch recorded in `deferred-work.md` (Tariff Check card).
4. **Given** the persist request fails, **when** the failure is returned, **then** the strip reverts to the persisted Locale, an inline error appears under the row ("Couldn't change the language…", rendered in the still-persisted language), and no UI language or format changes occur.
5. **Given** the device is offline, **when** the Profile menu is opened, **then** the Language strip is disabled and its sub-line reads "Needs a connection" (de: "Benötigt eine Verbindung"); the Appearance row is unaffected.
6. **Given** another Household member has the app open, **when** the Locale is changed elsewhere, **then** they receive the new Locale on their next load or session refresh — no live push is required.
7. **Given** the Locale is changed, **when** stored data is inspected, **then** it is unchanged (locale-neutral storage, NFR5) and any offline-queued Meter Reading (AD-16) is unaffected.
8. **And** every new string exists in both catalogs (AD-18); the endpoint validates the Locale against the supported list (`de-DE`, `en-US`) and rejects anything else (FR-34, UX-DR32).

## Tasks / Subtasks

- [x] **Task 1: Backend — `SetHouseholdLocale` use case + repository method (AC #2, #8)**
  - [x] `src/EnergyTracker.Application/SetHouseholdLocale.cs` — imperative-verb use case, single `ExecuteAsync(Guid householdId, string locale, CancellationToken)`, one-line `/// <summary>` citing the ACs (project-context C# documentation rule). Validate with `CreateHousehold.SupportedLocales.Contains(locale)` (exact, case-sensitive match — `de-de` and `fr-FR` are rejected; a JSON `null` reaches here as a real null, so the check must be null-safe) → throw `HouseholdValidationException($"Unsupported locale '{locale}'. Supported locales: …")` — reuse the exact message shape from `CreateHousehold.cs:26-30`. **Do not copy the list**: reference `CreateHousehold.SupportedLocales` so there is one closed set.
  - [x] `IHouseholdRepository.UpdateLocaleAsync(Guid householdId, string locale, CancellationToken)` (`Application/Ports/IHouseholdRepository.cs`) + implementation in `Infrastructure/Adapters/HouseholdRepository.cs` (next to `UpdateYearlyBaselineAsync`, line ~97): `SingleAsync` the household, set `Locale`, `SaveChangesAsync`. See **Dev Notes → Concurrency decision**: this write deliberately takes **no** `expectedVersion` and does **not** bump `Household.Version`. No migration (the `Locale` column already exists) — **no `scripts/add-migration.sh` run**.
  - [x] Return the updated `Household` so the endpoint can answer with the standard `HouseholdResponse`.

- [x] **Task 2: Backend — endpoint (AC #2, #8)** — `src/EnergyTracker.Api/Endpoints/HouseholdEndpoints.cs`
  - [x] `api.MapPut("/households/{id}/locale", …)` alongside `yearly-baseline` / `ai-plausibility`. First statement: `TryAuthorizeHousehold(id, householdAccessor, out var forbidden)` (Household has no AD-3 query filter on itself — skipping this lets any authenticated caller change another household's language). Body record `SetHouseholdLocaleRequest(string Locale)` declared at the bottom of the file with the other request records.
  - [x] Success → `Results.Ok(ToDetailsResponse(household))` (same `HouseholdResponse` shape as every other Household route — do not invent a new DTO). `HouseholdValidationException` → `Results.Problem(detail: ex.Message, statusCode: 400)` (RFC 7807, same as `yearly-baseline`). Register `SetHouseholdLocale` in DI exactly where `SetYearlyBaseline`/`SetAiPlausibilityEnabled` are registered (grep `AddScoped<SetYearlyBaseline>` in `Program.cs`/service registration).
  - [x] No role check — every member may call it (epic AC). Route stays behind the `/api` group's auth requirement (project-context Security: no unauthenticated endpoint).

- [x] **Task 3: Backend tests (AC #2, #7, #8)**
  - [x] `tests/EnergyTracker.Application.Tests/SetHouseholdLocaleTests.cs` — NSubstitute against `IHouseholdRepository`, Shouldly, `Snake_case_with_underscores` names, `TestContext.Current.CancellationToken`: persists a supported locale (repo `Received(1)`); rejects `fr-FR`, `de-de`, empty, and `null` with `HouseholdValidationException` and **never** calls the repo (`DidNotReceive`). Model on `SetYearlyBaselineTests.cs`.
  - [x] `tests/EnergyTracker.Api.Tests/HouseholdLocaleEndpointsTests.cs` (real `EnergyTrackerApiFactory`/Testcontainers, model on `YearlyBaselineEndpointsTests.cs` — same `CreateHouseholdAsync` helper): PUT `en-US` on a `de-DE` household → 200, body `Locale == "en-US"`, `Version` **unchanged**, a subsequent `GET /api/households/{id}` **and** `GET /api/session` both report `en-US`; unsupported locale → 400 `ProblemDetails` and the stored Locale unchanged; another household's id → 403; a second member who joined via invite (see `HouseholdInviteTests.cs` for the helper idiom) can PUT (no admin role); unauthenticated → 401 (mirror whichever unauthenticated assertion `AuthenticationTests.cs` uses).
  - [x] Stored-data-unchanged (AC #7): assert in the API test that a Yearly Baseline and a Meter Reading created before the locale switch read back identically after it (same `kwhValue`, same timestamp offset).
  - [x] Infrastructure (real DbContext) test in `HouseholdRepositoryTests.cs` for `UpdateLocaleAsync`: updates `Locale`, leaves `Version`/`YearlyBaselineKwh` untouched, throws nothing on an unchanged value (idempotent). Runs against whichever providers that file already covers.

- [x] **Task 4: i18n — household locale drives the active language, `<html lang>` follows (AC #2, #3)** — `web/src/i18n/index.ts`, `web/src/App.tsx`
  - [x] **Do not touch the `LanguageDetector` chain** (see Dev Notes → i18n facts): it stays as the pre-Household source. In `App.tsx` add one effect: when `state.status === 'ready'`, `void i18next.changeLanguage(state.household.locale)`. One seam covers all three ways a ready household appears — session load, `HouseholdCreationForm.onCreated`, `InviteAcceptForm.onJoined`. Import the default export from `@/i18n` (or use `useTranslation().i18n`). Guard against an unsupported stored value: only call it when `supportedLocales.includes(locale)`.
  - [x] `i18n/index.ts`: add `i18next.on('languageChanged', (lng) => { document.documentElement.lang = lng })` and set it once after init. `web/index.html` hardcodes `<html lang="en">` today — without this, a German UI is read by screen readers with an English voice and the AC #2 live-region announcement is mispronounced. This is part of "switch the language", not optional polish.
  - [x] Household-creation screen behavior is preserved: before `ready`, nothing calls `changeLanguage`, so Story 1.5's detector-as-display-language behavior (and its tests) are unchanged. Verify that test still passes without edits.

- [x] **Task 5: Shared household-locale seam (AC #2, #3, #6)** — new `web/src/lib/household-locale-context.ts` (+ App wiring)
  - [x] Why: the Language row must tell `App` the new Locale so `state.household.locale` (the source of the `locale` **prop** that `DashboardPage`, `TrendHistoryPage`, `TariffRadarPage` and their children feed to `Intl.NumberFormat`/`Intl.DateTimeFormat`) updates — otherwise the UI language flips but dates/numbers stay in the old locale, which is exactly the mismatch this story exists to remove. Prop-drilling `locale` + `onLocaleChanged` through four pages → `NavChrome` → `ProfileMenu` (and again for 8.12's Settings card) touches ~10 files and their tests; a tiny context is the proportionate seam.
  - [x] `createContext<{ locale: string | null; setLocale: (locale: string) => void }>({ locale: null, setLocale: () => {} })` plus a `useHouseholdLocale()` hook, in a `.ts` file (oxlint `react/only-export-components` is `warn` — don't co-locate a component export). `App.tsx` provides `{ locale: state.household.locale, setLocale }` around the `ready` tree, where `setLocale` updates `state.household.locale` in place (`setState(prev => prev.status === 'ready' ? { ...prev, household: { ...prev.household, locale } } : prev)`).
  - [x] `ProfileMenu`'s props stay **unchanged** (`email`, `householdId`, `supportsFederatedLogout`); the Language row reads `householdId` from ProfileMenu and locale/setLocale from the context. Existing `profile-menu.test.tsx` renders `<ProfileMenu …/>` with no provider — with the `null` default the row must render in a safe state (see Task 7) rather than crash, and every existing test must pass unchanged.
  - [x] Offline-queued readings (AC #7): `meter-reading-sync`/`offline-queue` never read the locale — confirm by grep and leave them alone. `log-reading-sheet.tsx:77` already formats with `i18n.language`, which now tracks `Household.Locale` — no change needed.

- [x] **Task 6: Frontend API client (AC #2, #4)** — new `web/src/lib/household-locale-api.ts` (+ `household-locale-api.test.ts`)
  - [x] `updateHouseholdLocale(householdId: string, locale: string): Promise<void>` — `fetch(\`/api/households/${householdId}/locale\`, { method: 'PUT', credentials: 'include', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ locale }) })`; throw on any non-ok status **and** let network errors propagate (both are "failure" for AC #4). Match the style/credentials convention of the sibling `lib/*-api.ts` files (read `tariff-check-api.ts` first). A 401 is treated as a plain failure here — do not add a login redirect in this story.

- [x] **Task 7: Extend `PreferenceStrip` — close the 8.10 review deferrals (AC #2, #5)** — `web/src/components/preferences/preference-strip.tsx` (+ tests)
  Story 8.10's review explicitly deferred these "until 8.11 makes the strip async" (`deferred-work.md` → "Deferred from: code review of 8-10…"); they are now in scope:
  - [x] **Ignore input while `pendingValue` is set**: clicks and arrow/Home/End keys must not fire a second `onChange` mid-save. Keys are still `preventDefault`/`stopPropagation`'d (so Radix doesn't steal them) but `select()` becomes a no-op. Unit test: click and ArrowRight while pending → `onChange` called 0 extra times.
  - [x] **Arrow index from the focused segment, not from `value`**: derive `currentIndex` from `refs.current.indexOf(document.activeElement)` (fall back to the checked index). With an optimistic `value` that is ahead of/behind focus after a revert, `value`-derived indexing moves the wrong way.
  - [x] **Saving announcement**: the pending segment's accessible name stays the endonym, but add `aria-busy` on the group (already present) **and** a caller-supplied status via the row's live region (Task 8) — do not put a second live region inside the strip.
  - [x] **No checked segment** (stored `Household.Locale` outside the option list): make the first enabled segment `tabIndex=0` so the strip is never unreachable.
  - [x] **Code segments**: the spec says two-letter codes are 12px/**700**; the strip currently applies `font-semibold` (600) to every segment. Apply `font-bold` when `option.code` is rendered (icons unaffected). No new tokens.
  - [x] **Reduced motion**: the `Loader2` spinner uses `animate-spin` unconditionally — gate it `motion-safe:animate-spin` (EXPERIENCE.md motion contract; also closes the 8.10 "low" deferral). The pending state must remain visible without rotation (static icon + 65% opacity).
  - [x] Keep every existing `preference-strip.test.tsx` and `appearance-row.test.tsx` test passing unchanged.

- [x] **Task 8: `LanguageRow` + `useOnlineStatus` (AC #1, #2, #4, #5)** — `web/src/components/preferences/language-row.tsx`, `web/src/hooks/use-online-status.ts`
  - [x] `use-online-status.ts`: `useSyncExternalStore` over `window` `online`/`offline` events, snapshot `navigator.onLine`, server snapshot `true` (same idiom as `use-wide-breakpoint.ts` / `use-theme-preference.ts`). No such hook exists today — `navigator.onLine` is only read ad hoc in `meter-reading-sync.ts:59` and `logoff.ts:14`; do not refactor those.
  - [x] `language-row.tsx` composes `PreferenceRow` (icon `Globe`, 14px; verify the name exists in the installed lucide) + `PreferenceStrip<'de-DE' | 'en-US'>` with options `[{ value: 'de-DE', code: 'DE', label: 'Deutsch' }, { value: 'en-US', code: 'EN', label: 'English' }]`. Labels come from catalog keys whose value is the literal endonym in **both** catalogs (never `t()`-translated per language). Give the row its own `useId()` label id (8.10 lesson: each strip needs an explicit `aria-labelledby`, not DOM-order naming). `data-testid="language-strip"`.
  - [x] State machine (local to the row): `pendingLocale: string | null`, `error: boolean`. Strip `value={pendingLocale ?? persistedLocale}`, `pendingValue={pendingLocale ?? undefined}`. On select: clear `error`, set `pendingLocale`, `await updateHouseholdLocale(householdId, next)`. Success → `setLocale(next)` from the context (App effect performs `changeLanguage`), clear pending, set the announcement. Failure (network error or non-2xx) → clear pending (strip snaps back to `persistedLocale`), set `error`. Guard against setState after unmount (the menu can close mid-request) and against a stale response overwriting a newer one.
  - [x] **Persisted language is the source of "still on X" in the error**: `{{language}}` = the endonym of `persistedLocale` ("Deutsch"/"English"); the message renders via the current (unchanged) `t`, i.e. in the still-persisted language (AC #4).
  - [x] Error banner under the row: `role="alert"`, existing error tint tokens (grep how other forms render inline errors, e.g. `text-destructive` / the error-tint classes used in `tariff-configuration-form.tsx`; the DESIGN spec says "existing error tint — the one genuine-system-error use of red"; **no new tokens**). Must not wrap the row or widen the 296px menu.
  - [x] **Live region (AC #2)**: one visually hidden `role="status"` (`aria-live="polite"`, `sr-only`) **rendered by `ProfileMenu` outside `DropdownMenuContent`** (sibling of the `DropdownMenu`, always mounted) so the announcement survives the member pressing Escape right after switching. The row writes into it via a small callback prop or context value. Message comes from `i18next.t('preferences.language.announce', { lng: next, language: endonym(next) })` — forced into the **new** language regardless of whether `changeLanguage` has finished (de: "Sprache: Deutsch", en: "Language: English").
  - [x] **Offline (AC #5)**: `useOnlineStatus() === false` → strip `disabled`, sub-label becomes `t('preferences.language.offline')` ("Needs a connection" / "Benötigt eine Verbindung"), tracks `online`/`offline` events live while the menu is open. `navigator.onLine === true` is not proof of connectivity (captive portals) — the failure path in AC #4 is the backstop; do not add a probe request. The Appearance row must not read this hook.
  - [x] No provider in tests/older code (`locale: null` context default): render the strip with nothing checked and do not crash.

- [x] **Task 9: Integrate into `ProfileMenu` (AC #1, #5)** — `web/src/components/dashboard/profile-menu.tsx`
  - [x] Current state (8.10): `DropdownMenuContent` (`w-[296px]`, `onKeyDown={cycleTabStops}`) → optional email label + separator → `<div role="none"><AppearanceRow /></div>` → separator → disabled "Profile" item → destructive "Log off" item. The comment at that spot says 8.11 adds Language directly below Appearance. Add `<LanguageRow householdId={householdId} … />` inside the **same** `role="none"` wrapper, below `<AppearanceRow />`. Order must match the mockup: email → Appearance → Language → separator → Profile → Log off.
  - [x] **`cycleTabStops` must skip disabled strips** (real bug waiting to happen): its selector `'[role="radiogroup"] [role="radio"][tabindex="0"]'` also matches the checked segment of the **disabled** offline Language strip; `.focus()` on a disabled button is a silent no-op, so Tab would appear to hang on Appearance. Filter with `:not([disabled])`. Tab order with two live strips: Appearance → Language → first enabled menu item → Appearance (the 8.10 review accepted the Tab cycle "and revisit for two strips in 8.11" — this is that revisit; AC #2 of 8.10 was reworded to document it, do **not** change the approach, just make it correct for N strips and disabled strips). Add a unit test: Tab visits both strips then the first enabled menu item; with Language disabled, Tab skips it; Shift+Tab reverses.
  - [x] Add the always-mounted live region (Task 8) next to the `DropdownMenu` in the returned fragment, before the `Dialog`. Do not disturb the avatar trigger, email-omitted behavior, disabled Profile row, `useLogoff` wiring, `requestAnimationFrame(openLogoffDialog)` flow, or the logoff `Dialog` markup.
  - [x] Selecting a segment must keep the menu open (same proven behavior as Appearance — re-assert in a test for the Language strip, including after the async save resolves).

- [x] **Task 10: i18n catalogs (AC #1, #4, #5, #8)** — `web/src/locales/en-US/translation.json`, `web/src/locales/de-DE/translation.json`
  - [x] Extend the existing top-level `"preferences"` block (8.10 created `preferences.appearance.*`; keep siblings consistent and add a `preferences.language` sub-block). Keys: `label` (en "Language" / de "Sprache"), `scope` (en "Whole household" / de "Ganzer Haushalt"), `offline` (en "Needs a connection" / de "Benötigt eine Verbindung"), `option.de` = "Deutsch" and `option.en` = "English" (**identical literal in both catalogs**), `error` (en "Couldn't change the language. Your household is still on {{language}} — try again." / de "Die Sprache konnte nicht geändert werden. Dein Haushalt nutzt weiterhin {{language}} — bitte erneut versuchen."), `announce` (en "Language: {{language}}" / de "Sprache: {{language}}"). Copy taken from `mockups/key-profile-preferences.html` §1/§2; informal "du/Dein" matches the existing de tone (8.10 note). The mockup shows "English" inside the de error sentence — `{{language}}` is the endonym, never translated.
  - [x] Diff the two catalogs' key sets (no key-parity test exists: `translation-consistency.test.ts` only covers WCAG label-in-name — don't invent a framework; do a manual/scripted diff and note it in Completion Notes).
  - [x] Existing Household-creation strings `householdCreation.localeOption.*` are unrelated (select items at creation) — leave them.

- [x] **Task 11: Frontend tests (AC #1-#5)** — colocated, Vitest + Testing Library (repo convention)
  - [x] `language-row.test.tsx`: two options `DE`/`EN` with accessible names `Deutsch`/`English`; persisted locale checked; sub-label; select other → strip shows it selected **immediately** with the spinner and `aria-busy`, `fetch` called once with `PUT /api/households/{id}/locale` and `{ locale }` body; success → context `setLocale` called, pending cleared, live region text is the new-language announcement; failure (500 **and** rejected fetch) → strip reverts to persisted, `role="alert"` error shown in the persisted language, `setLocale` **not** called, `i18n.language` unchanged; second click/arrow while pending ignored; offline (`vi.stubGlobal('navigator', …)`/`Object.defineProperty(navigator, 'onLine', …)` + dispatch `offline`/`online`) → strip disabled + "Needs a connection", re-enables on `online`; Appearance row unaffected offline.
  - [x] `use-online-status.test.ts`: snapshot follows `online`/`offline` events.
  - [x] `profile-menu.test.tsx`: **all existing tests pass unchanged.** Add: Language row is rendered below Appearance (DOM order) and above Profile; selecting a language keeps the menu open; Escape closes and returns focus to "Account menu"; Tab behavior per Task 9.
  - [x] `App`-level test (extend `App.test.tsx` if present — `grep -l "api/session" web/src/*.test.tsx` — or add to the nearest existing one): with a mocked session `{ locale: 'de-DE' }` and a browser language of `en`, the active language becomes `de-DE` once ready (AC #3); with `hasHousehold:false`, the detector language is still used and `changeLanguage` is **not** called (Story 1.5 behavior); after creating a household with `de-DE`, the language switches to it. Reset `i18n` language in `afterEach` (`web/src/test/setup.ts` imports `@/i18n` globally — leaking a changed language breaks unrelated suites).
  - [x] Number/date formatting (AC #2 "together"): render a locale-consuming surface (e.g. `StatusCard` or `TariffCheckCard`) under the App/provider, switch locale through the row, assert a formatted figure/date changes separator/format (e.g. `4.821` ↔ `4,821`) without remount.
  - [x] Playwright — extend `web/e2e/app-shell.spec.ts` (do not create a new spec file): at 1000px with a faked `/api/session` (`locale: 'de-DE'`, `email` set) and `page.route` for `PUT **/api/households/*/locale` → 200: open "Konto-Menü", click `English` → heading/nav labels switch to English, `document.documentElement.lang === 'en-US'`, menu still open; a 500 response variant → strip reverts, error visible, labels unchanged; `page.context().setOffline(true)` → Language strip disabled + "Benötigt eine Verbindung", Theme strip still works. Note the existing locator lesson: use `exact: true` for role names that are substrings of others.

- [x] **Task 12: Full regression + live Chrome verification (mandatory — do not defer)**
  - [x] `npm --prefix web run test`, `tsc -b`, `oxlint` (no new warnings), `dotnet test` for Application, Api, Infrastructure **and** `EnergyTracker.Architecture.Tests` (must stay 6/6 — this story adds no `localStorage` use; the `i18nextLng` cache is written by the i18next detector library, not by a source literal the guard scans), `npx playwright test` (move `certs/vite-dev-cert.pem`/`.key` aside first if present and restore immediately; check for an orphaned `vite preview` on port 4173 — see Stories 8.6–8.9 Debug Logs).
  - [x] **This changes persisted household state and is browser-dependent (live language switch, focus/keyboard in a Radix menu, `<html lang>`, screen-reader live region), and the session response drives it on load. Same gate as Stories 8.1–8.10: it cannot move review→done without a live Claude-in-Chrome verification actually performed.** If Chrome isn't connected, raise it immediately and pause for the user to connect it; do not defer or tick the box on unit/e2e proof alone (`project-context.md` Process gates).
  - [x] Verify live against the local stack (`scripts/run-api.sh` + Vite at https://localhost:5173; the approved OIDC test-user flow is documented in `docs/local-development.md`; open a **new** ≥660px tab — `resize_window` on an existing tab is unreliable per Stories 8.6/8.7): both Dark and Light; open Account menu → Language row renders per mockup §1 (de + en); switch DE→EN→DE: menu stays open, labels/headings/dates/numbers flip together with no reload on Dashboard, Trend History, Tariff Radar (Tariff Check card date + sentence now in the same language — the `deferred-work.md` mismatch), Settings; `document.documentElement.lang` follows; **reload → household language persists** (not the browser language); force a failure (stop the API or block the request via DevTools) → strip reverts + inline error in the still-persisted language; go offline (DevTools) → strip disabled + "Needs a connection", Theme still switchable; keyboard: Tab order Appearance → Language → Profile-area item, ←/→/Home/End on the Language strip, Escape returns focus to the avatar; 44×44 hit area measured. A second browser context/profile signed in as another member sees the new Locale after reload (AC #6).
  - [x] **This story writes a household-scoped setting on the real test household: restore the original Locale (de-DE) when done and say so in Completion Notes.** Do not modify readings, tariffs, or any other data.

- [x] **Task 13: Verify against every AC**
  - [x] Walk AC #1-#8 individually in Completion Notes and state what proves each (code reference, unit test, API test, e2e assertion, or live check) — same AC-by-AC accounting as Stories 8.1–8.10.

## Dev Notes

### Scope

Backend **and** frontend (unlike 8.10). New: one Application use case, one repository method, one PUT route, one context, one API client, one hook, one row component, strip hardening. **No migration, no new `Household` column, no DTO change** (the session/`HouseholdResponse` already carry `Locale`). Do not implement Story 8.12's Settings Preferences card — but build `LanguageRow` so 8.12 can mount it unchanged (it reads everything from props/context, nothing menu-specific).

### Concurrency decision (AD-4) — read before coding Task 1

AD-4 says concurrency-sensitive entities, including "Household settings", carry an EF `int Version` token, and `UpdateYearlyBaselineAsync` / `UpdateAiPlausibilityEnabledAsync` both take `expectedVersion`, pin `OriginalValue`, `Version++`, and map `DbUpdateConcurrencyException` → `HouseholdConcurrencyConflictException` → 409. **Locale is deliberately different in this story:**
- The epic's acceptance criteria and technical note specify a body of just the locale and no 409 path; the session response the SPA holds (`SessionResponse`) does **not** carry `Version`, so requiring one would mean either widening the session contract or a GET round-trip before every toggle.
- The operation is idempotent and single-field: two members choosing different languages concurrently simply end on the last choice; nothing is lost or silently merged, and every member sees the persisted value on next load (AC #6).
- Not bumping `Version` keeps a language change from invalidating another member's in-flight Yearly Baseline / AI-toggle edit (which would 409 for an unrelated reason). EF only writes the modified `Locale` column; the existing `Version` concurrency token remains in the UPDATE's `WHERE`, so a concurrent baseline edit still detects a real conflict on its own field.
- Record this as a documented deviation in Completion Notes. **If the reviewer disagrees**, the alternative is: add `Version` to `SessionResponse`, carry it in the App household state (refreshed from every Household-returning response), send `{ locale, version }`, bump, and surface 409 through the same revert + error path (AC #4) — a larger change, not recommended for a setting with no data-loss mode.

### Read-before-modify current state (Step 3 analysis; files marked UPDATE)

- `src/EnergyTracker.Domain/Household.cs` — `Locale` is `required string … { get; set; }` (already mutable; the comment says it is intentionally a string, not an enum — keep). No change.
- `src/EnergyTracker.Application/CreateHousehold.cs` — owns `public static readonly IReadOnlyCollection<string> SupportedLocales = ["de-DE", "en-US"]` and the unsupported-locale message. **Reuse, do not duplicate.** (A later locale is a resource-file addition per AD-18; the list is the one place the backend has to learn about it.)
- `src/EnergyTracker.Application/Ports/IHouseholdRepository.cs` + `Infrastructure/Adapters/HouseholdRepository.cs` — UPDATE: add `UpdateLocaleAsync`. Existing methods shown above; preserve them untouched.
- `src/EnergyTracker.Api/Endpoints/HouseholdEndpoints.cs` — UPDATE: `TryAuthorizeHousehold`, `ToDetailsResponse`, the `HouseholdResponse(Id, Locale, Currency, YearlyBaselineKwh, Version)` record, and the two existing `PUT` routes are the patterns. Preserve all existing routes.
- `src/EnergyTracker.Api/Endpoints/SessionEndpoints.cs` — **no change**: it reads `household.Locale` fresh from the DB on every `GET /api/session`, so AC #6 ("next load") and AC #3 are satisfied server-side by construction. Assert it in the API test rather than changing it.
- `web/src/i18n/index.ts` — UPDATE (small): today `i18next.use(LanguageDetector).use(initReactI18next).init({ supportedLngs: ['de-DE','en-US'], fallbackLng: 'en-US', … })`; the comment says the detector is "purely the display language for this screen's own chrome" before a Household exists. Add only the `<html lang>` sync.
- `web/src/App.tsx` — UPDATE: holds `SessionState` (`ready` carries `household: { id, locale, currency }` = `CreatedHousehold` from `household-creation-form.tsx`); `locale` is passed to `TrendHistoryPage` (303), `TariffRadarPage` (321), and `DashboardPage` via `household`. `SettingsPage` receives **no** locale today (only `householdId`). The session fetch effect (lines ~145-198) sets state once and never refetches — that is fine (AC #6: next load).
- `web/src/components/dashboard/profile-menu.tsx`, `nav-chrome.tsx` — ProfileMenu UPDATE (Task 9); NavChrome **unchanged** (it passes `householdId`, `supportsFederatedLogout`, `email` — the context removes any need to thread locale through it).
- `web/src/components/preferences/{preference-strip,preference-row,appearance-row}.tsx` — strip UPDATE (Task 7); row and appearance-row unchanged. `PreferenceRow` uses `whitespace-nowrap` on both lines; "Whole household"/"Ganzer Haushalt"/"Needs a connection"/"Benötigt eine Verbindung" must still fit on one line in the 296px menu (label column + 3 × 44px strip is sized for the widest theme strip; the two-segment language strip is narrower). Wrap/truncation fallback and the Settings-card overflow are 8.12's problem (deferred item) — do not refactor the row here beyond what the new strings need.
- `web/src/hooks/use-theme-preference.ts`, `lib/color-scheme.ts` — unchanged; the Appearance row must stay online/offline-agnostic.

### i18n facts (verified in the repo)

- `LanguageDetector` default options cache the resolved language in `localStorage` under `i18nextLng`, and `changeLanguage()` rewrites it. That is library behavior, not a string in `web/src` (the `FrontendDoesNotStoreAuthTokensTests` guard scans source literals only — keep it passing, do **not** extend its allowlist). Consequence: after the first ready session on a device, the pre-ready "Loading…" screen is shown in the last household language rather than the browser language. That is acceptable and a slight UX win; do not turn the cache off (Story 8.10's live verification switched languages via `i18nextLng` — keep that test idiom valid). The authoritative post-ready language is enforced by the App effect, not by detector precedence.
- `react-i18next`'s `useTranslation` re-renders consumers on `languageChanged`, so all `t(...)` copy flips with no extra plumbing. Number/date formats flip only because `state.household.locale` (props) changes — that is why Task 5 exists. Grep for `useMemo`/`useCallback` dependency arrays or cached formatter instances keyed on an old `locale` (`new Intl.*` calls are created per render in the files inspected — `status-card.tsx:94`, `tariff-check-card.tsx:21`, `trend-chart.tsx:125-179`, `tariff-history-list.tsx:74-76` — so no stale formatter is expected, but confirm during dev).
- `t(key, { lng })` forces a language for one call — use it for the live-region announcement so it never races `changeLanguage`.
- Library versions (project-context): i18next 26.x, react-i18next 17.x. No upgrade needed; no web research required beyond the existing stack.

### Architecture compliance

- **AD-18:** `Household.Locale` is the single field driving both UI language and number/date formatting; catalogs stay additive `web/src/locales/{locale}/translation.json`; no code change needed to add a locale beyond `SupportedLocales` and `supportedLocales`.
- **AD-3 / security:** the route requires the caller's own Household (`TryAuthorizeHousehold`); never `IgnoreQueryFilters`/`Find` on Household-scoped entities (Household itself is the tenant root with no filter, hence the explicit check).
- **AD-15:** Locale is household-scoped config, never a code literal — the closed list in `CreateHousehold.SupportedLocales` is the validation set, not a hardcoded per-household value.
- **AD-16 / NFR5:** stored data is locale-neutral; this story writes exactly one column on one row. Offline reading queue untouched.
- **AD-1:** the use case depends only on `IHouseholdRepository`; do not reference `Infrastructure`/EF from Application. `DbUpdateConcurrencyException` is not involved (no version check).
- **Errors:** RFC 7807 `ProblemDetails` for 400/403 (existing pattern via `Results.Problem`).
- **Testing conventions (project-context):** Shouldly (never `Assert.*`), NSubstitute, `Snake_case_with_underscores`, `TestContext.Current.CancellationToken`, Testcontainers for DB-touching tests, frontend tests colocated.
- **Frontend conventions:** `import type` for types (`verbatimModuleSyntax`), no enums/param properties (`erasableSyntaxOnly`), `@/` alias, oxlint (not ESLint), new feature UI under `components/preferences/` (not `components/ui`), shadcn primitives only via `npx shadcn add` if genuinely needed (none expected).

### Previous story intelligence (8.10 + 8.1)

- **Radix `role="menu"` hosting radiogroups** is the riskiest area and already solved: strip stops propagation for handled keys and printable keys (typeahead), ignores alt/ctrl/meta chords, segments are plain `<button role="radio">` so selecting never closes the menu, and `cycleTabStops` makes Tab reachable. 8.10's review flagged that live verification found real bugs unit tests missed (Tab unreachable; 42px hit area) — expect the same here and do the live pass (Task 12) before claiming done.
- 8.10 live-verification workarounds still apply: new correctly-sized tab instead of `resize_window`; `exact: true` in Playwright locators (substring collisions like "System — currently Dark"); certs moved aside for Playwright; check orphaned `vite preview` on port 4173.
- 8.10 review applied: the theme guard allowlist compares exact path; `initColorScheme` registers its OS listener once. Don't regress them — this story does not touch `color-scheme.ts`.
- The "Tab cycle" in the Profile menu was an accepted documented deviation from the literal "Tab enters the group on the checked segment" wording; 8.11's epic/mockup AC repeats that wording. Keep the accepted behavior; note it once more in Completion Notes rather than re-litigating.
- `deferred-work.md` items from 8.10 that this story **resolves**: strip input-while-pending guard, focused-index arrows, reduced-motion spinner (Task 7). Items it **leaves**: Radix-menu/radiogroup ARIA semantics (design-level; 8.12 revisits), `PreferenceRow` nowrap overflow (8.12), inline-script/`color-scheme.ts` drift test. After implementing, mark the resolved ones in `deferred-work.md` the way earlier stories do (grep how a prior story closed an item before editing the file).
- Process gate (project-context): any post-merge fix to this story needs a linked spec/story doc.

### Git intelligence

Recent work (`b147dd8` Story 8.10, `3fdee3d` 8.9, `26891d8` 8.8) are all single-squash story PRs touching `web/src/components/*`, locale catalogs, `web/e2e/app-shell.spec.ts`, and the story/sprint-status files. 8.10 added `components/preferences/*`, `hooks/use-theme-preference.ts`, `lib/color-scheme.ts`, the Profile-menu Appearance row and the architecture-guard allowlist — 8.11 builds directly on those files. Commit message style: `Story 8.11: Language toggle — household locale switch (UX-DR32, FR-34) (#NN)`.

### Testing standards summary

- jsdom has no layout/media queries — assert structure/classes; prove geometry (44×44 hit area, 296px menu, no wrapping in de-DE) in Playwright/live.
- Radix floating primitives need `hasPointerCapture`/`scrollIntoView` stubs in jsdom (already in `profile-menu.test.tsx`'s `beforeEach`; reuse).
- `matchMedia` is undefined in jsdom unless stubbed; `navigator.onLine` is configurable via `Object.defineProperty(navigator, 'onLine', { configurable: true, value })` — restore in `afterEach`.
- `web/src/test/setup.ts` imports `@/i18n` globally (language shared across a test file): always `await i18next.changeLanguage('en-US')` in `afterEach` when a test changes it, or other suites' English assertions break.
- Reset the theme (`setThemePreference('system')`) as the existing profile-menu tests do.

### Project Structure Notes

- New: `src/EnergyTracker.Application/SetHouseholdLocale.cs`; `web/src/lib/household-locale-context.ts`; `web/src/lib/household-locale-api.ts` (+ test); `web/src/hooks/use-online-status.ts` (+ test); `web/src/components/preferences/language-row.tsx` (+ `language-row.test.tsx`); tests `SetHouseholdLocaleTests.cs`, `HouseholdLocaleEndpointsTests.cs`.
- Modified: `IHouseholdRepository.cs`, `HouseholdRepository.cs`, `HouseholdEndpoints.cs`, DI registration file, `HouseholdRepositoryTests.cs`; `web/src/i18n/index.ts`, `web/src/App.tsx`, `components/dashboard/profile-menu.tsx` (+ test), `components/preferences/preference-strip.tsx` (+ test), both locale JSON files, `web/e2e/app-shell.spec.ts`, `_bmad-artifacts/implementation/deferred-work.md`, sprint-status + this story file.
- Variances to record: (1) no `Version` check on the locale write (see Concurrency decision); (2) a React context rather than prop drilling for the locale seam (justified above); (3) `<html lang>` sync is added though not in the epic ACs — required for a correct language switch.

### References

- [Source: _bmad-artifacts/planning/epics/epic-8-desktop-tablet-layout-correction.md#Story 8.11]
- [Source: _bmad-artifacts/planning/prds/prd-energy-tracker-2026-08-08/prd/4-features.md#FR-34]
- [Source: _bmad-artifacts/planning/epics/requirements-inventory.md — UX-DR32, UX-DR33, FR-34]
- [Source: _bmad-artifacts/planning/ux-designs/ux-energy-tracker-2026-08-08/DESIGN/components.md — Profile menu, Preference icon strip]
- [Source: _bmad-artifacts/planning/ux-designs/ux-energy-tracker-2026-08-08/mockups/key-profile-preferences.html §1, §2, §4]
- [Source: _bmad-artifacts/implementation/8-10-theme-toggle-profile-menu.md — strip/row components, Radix gotchas, live-verification notes]
- [Source: _bmad-artifacts/implementation/deferred-work.md — "Deferred from: code review of 8-10…" and the Tariff Check card locale-mismatch entry]
- [Source: _bmad-artifacts/project-context.md — AD-3/AD-4/AD-15/AD-18, Testing, Process gates]
- [Source: src/EnergyTracker.Application/CreateHousehold.cs, SetYearlyBaseline.cs; src/EnergyTracker.Api/Endpoints/HouseholdEndpoints.cs, SessionEndpoints.cs; src/EnergyTracker.Infrastructure/Adapters/HouseholdRepository.cs]
- [Source: web/src/App.tsx, web/src/i18n/index.ts, web/src/components/dashboard/profile-menu.tsx, web/src/components/preferences/preference-strip.tsx]

## Dev Agent Record

### Agent Model Used

claude-sonnet-5-5

### Debug Log References

### Completion Notes List

- Backend: `SetHouseholdLocale` + `IHouseholdRepository.UpdateLocaleAsync` + `PUT /api/households/{id}/locale` (TryAuthorizeHousehold, 400 ProblemDetails). **Documented deviation (Concurrency decision):** no `expectedVersion`, no `Version` bump, no 409 path; last write wins. No migration.
- Frontend: `HouseholdLocaleContext` seam (App provides `{locale,setLocale}`; App effect calls `i18next.changeLanguage` when ready and locale is supported); `<html lang>` synced on `languageChanged`; `updateHouseholdLocale` client; `useOnlineStatus`; `LanguageRow` (optimistic + spinner, revert + `role="alert"` error, offline disabled, live region owned by ProfileMenu outside the menu content); `PreferenceStrip` hardening (pending guard, focused-index arrows, no-checked tabindex fallback, bold code segments, `motion-safe` spinner); `cycleTabStops` skips disabled strips. Accepted 8.10 Tab-cycle behavior unchanged.
- **Two existing 8.10 `profile-menu.test.tsx` assertions had to change** (story said "unchanged", but they encode a single-strip menu): the Tab-cycle test now covers Appearance → Language → Log off, and the "3 radios" count is scoped to the Appearance group.
- Catalog key sets: `preferences.language.*` added identically (6 keys) to both catalogs; diff verified via identical 11-line additions.
- Variances: no Version check; context instead of prop drilling; `<html lang>` sync added.
- Deferred-work.md: marked resolved the 8.10 strip-pending/focused-index/reduced-motion items and the Tariff Check card locale mismatch.
- Tests: dotnet Api 234, Application 399, Infrastructure 246, Architecture 6/6 green; web vitest 507 green; `tsc -b` clean; oxlint no new warnings; Playwright 13 green (3 new, certs moved aside and restored).
- **Live Claude-in-Chrome verification (2026-09-30, ≥660px, light scheme, local stack):** Account menu renders Appearance + Language (DE/EN, "Ganzer Haushalt"/"Whole household"); DE→EN→DE switches flip UI copy, Tariff Check card date/sentence (`12.12.2026` ↔ `Dec 12, 2026`), `<html lang>` and the live region ("Language: English"/"Sprache: Deutsch") with no reload, menu stays open; **reload with a stale `i18nextLng=de-DE` cache and a `de` browser still came up `en-US`** (household locale wins, persisted via `/api/session`); failed save (PUT rejected in-page) → strip reverted to EN + inline error "Couldn't change the language. Your household is still on English — try again."; offline (simulated via `navigator.onLine` + `offline`/`online` events) → Language strip disabled + "Needs a connection", Theme strip still enabled, recovers on online; keyboard: Tab Appearance → Language → Log off, Shift+Tab back, ArrowLeft on Language selected and persisted DE, Escape closed the menu and returned focus to "Konto-Menü"; segment hit area 44×44 (40px + 2×3px ::before). **Not done live:** Dark scheme pass, per-page flip on Trend History/Tariff Radar/Settings (covered by the vitest harness with real StatusCard + Playwright), second-member browser (AC #6, covered by the API test), true network-offline/API-down (simulated instead). The session used the account currently signed in (not necessarily the OIDC test user).
- **Test household Locale restored to de-DE** (verified via `/api/session`).
- AC walk: #1 language-row.test + profile-menu.test + live; #2 language-row(+formatting).test, e2e, live; #3 App.test (session/invite), live reload; #4 language-row.test, e2e, live; #5 language-row.test, e2e, live (simulated); #6 API test (session reflects new locale); #7 API test (baseline + reading unchanged; queue code never reads locale); #8 SetHouseholdLocaleTests + API 400 tests, catalogs both updated.

### File List

- src/EnergyTracker.Application/SetHouseholdLocale.cs (new)
- src/EnergyTracker.Application/Ports/IHouseholdRepository.cs
- src/EnergyTracker.Infrastructure/Adapters/HouseholdRepository.cs
- src/EnergyTracker.Api/Program.cs
- src/EnergyTracker.Api/Endpoints/HouseholdEndpoints.cs
- tests/EnergyTracker.Application.Tests/SetHouseholdLocaleTests.cs (new)
- tests/EnergyTracker.Api.Tests/HouseholdLocaleEndpointsTests.cs (new)
- tests/EnergyTracker.Infrastructure.Tests/HouseholdRepositoryTests.cs
- web/src/i18n/index.ts
- web/src/App.tsx, web/src/App.test.tsx
- web/src/lib/household-locale-context.ts (new), household-locale-api.ts (+test, new)
- web/src/hooks/use-online-status.ts (+test, new)
- web/src/components/preferences/language-row.tsx (+ language-row.test.tsx, language-row-formatting.test.tsx, new)
- web/src/components/preferences/preference-strip.tsx (+test)
- web/src/components/dashboard/profile-menu.tsx (+test)
- web/src/locales/en-US/translation.json, web/src/locales/de-DE/translation.json
- web/e2e/app-shell.spec.ts
- _bmad-artifacts/implementation/deferred-work.md, sprint-status.yaml, this story file

### Change Log

- 2026-09-30: Implemented Story 8.11 (backend locale endpoint, household-locale-driven i18n, Language row, strip hardening); live Chrome verification done; status → review.
