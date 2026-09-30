---
baseline_commit: fe1b93b
---

# Story 8.12: Preferences Card in Settings (<660px)

Status: done

<!-- Note: Validation is optional. Run validate-create-story for quality check before dev-story. -->

## Story

As a Household member on a phone,
I want the same Appearance and Language controls in Settings,
so that I can change them where I am, even though the Profile menu only exists at desktop/tablet width.

## Acceptance Criteria

1. **Given** the Settings screen at an available width <660px, **when** rendered, **then** a "Preferences" glass card appears in the Account group — an "Account" section label (de: "Konto") directly above the card, and the card directly above the unchanged Log off control from Story 1.12 — containing the Appearance row and the Language row with the same strips, sub-labels, states, and behavior as Stories 8.10 and 8.11 (UX-DR33). It reuses `AppearanceRow` and `LanguageRow` — **one shared component, no duplicated logic**.
2. **Given** the same screen at ≥660px, **when** rendered, **then** the Preferences card is **not rendered** (not merely hidden) — the Profile menu is the single home for both controls at that width. The "Account" label is not shown at ≥660px either.
3. **Given** the card at 320–340px width in both `de-DE` and `en-US` (Light and Dark), **when** rendered — including the Language row's offline state ("Needs a connection" / "Benötigt eine Verbindung") — **then** rows and sub-labels do not wrap or overflow (no horizontal scroll, no clipped text, the card never wider than its column), and segments keep their 44×44 hit area.
4. **Given** a member changes theme or language in the card, **when** they then widen the viewport to ≥660px (or reload), **then** the Profile menu shows the same persisted state (Theme per device via `localStorage`, Language per Household via `Household.Locale`).
5. **And** Story 1.12's Log off control and the Settings page's existing sections are otherwise unchanged (visually, behaviorally, and in their existing tests); Dark and Light are both verified (UX-DR33, FR-34, FR-35). Every new string exists in both catalogs (AD-18).

## Tasks / Subtasks

- [x] **Task 1: `PreferencesCard` component (AC #1, #3, #5)** — new `web/src/components/preferences/preferences-card.tsx`
  - [x] Props: `{ householdId: string }`. Renders a glass card containing `<AppearanceRow />` then `<LanguageRow householdId={householdId} onAnnounce={setAnnouncement} />` separated by the mockup's hairline (`.card .pref-row + .pref-row { border-top: 1px solid }`: `rgba(210,235,220,0.08)` dark / `rgba(40,70,50,0.08)` light — **arbitrary values from the mockup are fine; no new tokens**).
  - [x] Card surface: reuse the same glass card surface the sibling Settings cards use (mockup `.card`: radius 18px, padding 6px 10px, fill `rgba(220,245,230,0.06)`/border `rgba(210,235,220,0.13)` dark, `rgba(255,255,255,0.6)`/`rgba(40,70,50,0.12)` light). **First grep how `InviteMemberRow`/`AiPlausibilityForm`/`DataExportPanel` render their card surface** (shadcn `Card` vs. inline classes) and match it exactly; do not invent a second glass recipe.
  - [x] **Live region**: the card owns one visually-hidden `role="status"` / `aria-live="polite"` (`sr-only`), always mounted while the card is, fed by `LanguageRow`'s existing `onAnnounce` — the same idiom `ProfileMenu` uses (`profile-menu.tsx:68-76`). The card must not be a generic wrapper that forgets this: without it the AC #2 (8.11) "announced in the new language" behavior regresses on mobile.
  - [x] `data-testid="preferences-card"`. Plain container with no card-level `aria-label`/`role="group"`: each strip is already self-labelled via `PreferenceRow`'s `aria-labelledby`, and the visible heading is "Account"/"Konto" (not "Preferences"), so a group name would either duplicate it or collide with the page title. Add a group name only if the dev-time a11y check shows a real gap.
  - [x] Optional `className` passthrough on `AppearanceRow` / `LanguageRow` → `PreferenceRow` (it already takes `className`) **only if** needed for the in-card padding/divider (Task 3); keep defaults so `ProfileMenu` output is byte-identical.

- [x] **Task 2: Account group in `SettingsPage` (AC #1, #2, #5)** — `web/src/components/settings/settings-page.tsx`
  - [x] Current state (read in full): header → `div.flex.flex-col.gap-[var(--spacing-card-gap)]` containing four `wide:order-N` section blocks, then the bare `<Button variant="outline" className="wide:hidden self-start" onClick={openLogoffDialog}>` (Log off), then the logoff `Dialog` and `NavChrome`. There is **no** Account group today.
  - [x] Wrap the Log off button in a new Account group: `<div className="flex flex-col gap-[var(--spacing-card-gap)] wide:hidden">` → Account heading → `{!wide && <PreferencesCard householdId={householdId} />}` → the **unchanged** Log off `Button` (same classes, handler, icon, `t('settings.logoff.trigger')`). Keep the group as the last child of the card stack so DOM order is still "…Data → Account". Do not touch the `wide:order-*` wrappers, the `Dialog`, `useLogoff` wiring, or `NavChrome` props.
  - [x] **"Not rendered" means JS-gated, not `wide:hidden`-only**: use `useWideBreakpoint()` (`web/src/hooks/use-wide-breakpoint.ts`, `(min-width: 660px)`, mirrors `--breakpoint-wide`; jsdom has no `matchMedia` → returns `false`, i.e. narrow) and render `PreferencesCard` only when it is `false`. Reason: CSS-hiding would keep a second `Appearance`/`Language` radiogroup and a second live region mounted at ≥660px (duplicate `useThemePreference` subscribers, duplicate a11y-tree candidates in dual-render tests, and an unused `LanguageRow` effect tree). Keep the wrapper `wide:hidden` as well so the label/Log off hide without waiting for a JS re-render (same belt-and-braces the file already uses for Log off).
  - [x] **Do not reuse `SectionLabel`**: it is `hidden wide:block` (visible **only** at ≥660px — Story 8.5 replaced per-section `<h2>`s with it for the wide layout). The Account label must be visible **only below** 660px → new markup, same typography as `SectionLabel`/mockup `.section-label` (`text-[11px] font-bold tracking-[0.6px] text-muted-foreground uppercase`), as an `<h2 className="wide:hidden …">`. Either add a small `narrowOnly` variant prop to `SectionLabel` (update `section-label.test.tsx`) or inline the `<h2>`; prefer the prop only if it stays a 3-line change. Must stay an `<h2>` (heading semantics).
  - [x] Update the file's header comment block (it lists what the page covers) to mention the Account group / Preferences card (Story 8.12), matching the existing per-story comment style.

- [x] **Task 3: Fit at 320–340px in `de-DE` and `en-US` (AC #3)** — `preference-row.tsx`, card classes, catalogs
  - [x] This closes the 8.10 deferral "`PreferenceRow` uses `whitespace-nowrap` … will overflow in the Settings card below 660px" (`deferred-work.md` → "Deferred from: code review of 8-10…"). **Budget** (Settings column = viewport − 2×16px page padding; 320px → 288px): card border + padding ≈ 2 + 20 → 266px; row padding px-2 = 16 → 250px; `gap-3` = 12px; Appearance strip = 3×44 + 2×2 gaps + 2×2 padding + 2×1 border ≈ 142px ⇒ **label column ≈ 96px** for "[icon 14px + 6px gap] Darstellung" (12.5px/600 ≈ 70px ≈ 90px total — **tight**); Language strip ≈ 96px ⇒ ≈ 142px for "Benötigt eine Verbindung" (11px ≈ 125px — fits, measure). Numbers are estimates: **measure, don't trust**.
  - [x] Apply fixes in this order and stop at the first that passes live at 320px: (1) in-card rows drop the extra horizontal padding (card already supplies 10px) via the `className` passthrough (`px-0`), (2) `gap-2`, (3) `min-w-0` + `truncate` on the **label** only as last resort. **Never truncate or shorten the offline sub-label / scope sub-label** (changing 8.11's copy is out of scope) and **never let a sub-label wrap to two lines** (AC). If 320px genuinely cannot fit Appearance's label + strip in `de-DE` with (1)+(2), raise it in Completion Notes with the measured widths and stop for a decision rather than silently truncating "Darstellung".
  - [x] Any change to `PreferenceRow` defaults must leave the ProfileMenu's 296px layout pixel-identical (Playwright/live re-check of the open menu in `de-DE` and `en-US`; existing 8.10/8.11 tests unchanged).
  - [x] Hit area: segments stay 44×40 visual + `before:` inset extending to 44×44 (already in `PreferenceStrip`); the row's `min-h-12` (48px) must not be reduced. Do not change `PreferenceStrip` sizing.

- [x] **Task 4: i18n catalogs (AC #1, #5)** — `web/src/locales/en-US/translation.json`, `web/src/locales/de-DE/translation.json`
  - [x] Add exactly one key: `settings.sections.account` — en **"Account"**, de **"Konto"** (mockup §3 copy; `settings.sections` already holds `aiPlausibilityCheck`/`household`/`data`). The Preferences rows reuse the existing `preferences.appearance.*` / `preferences.language.*` keys from 8.10/8.11 — **do not add duplicate keys or a "Preferences" string** (the AC's word "Preferences" names the card; the visible label is "Account"/"Konto" per the mockup).
  - [x] Diff the two catalogs' key sets (no key-parity test exists; do a scripted diff and note it in Completion Notes).
  - [x] Watch the collision: the avatar button's accessible name is "Account menu" (`profileMenu.avatarLabel`) — a **button**, not a heading, so `getByRole('heading', { name: 'Account' })` is unambiguous; in Playwright use `exact: true` on any `getByText('Account')` / role names.

- [x] **Task 5: Frontend unit/component tests (AC #1-#5)** — colocated, Vitest + Testing Library
  - [x] `preferences-card.test.tsx`: renders the Appearance and Language radiogroups in that order with the same accessible names/sub-labels as the Profile menu ("This device", "Whole household"); Theme select applies immediately (assert via the existing `useThemePreference`/`data-theme`/class idiom from `appearance-row.test.tsx`; reset with `setThemePreference('system')` in `afterEach`); Language select runs the 8.11 flow through the context (stub `fetch`, `HouseholdLocaleContext.Provider`, assert `PUT /api/households/{id}/locale`, `setLocale`, and the card's **own** live region text "Language: English"/"Sprache: Deutsch"); failure → `role="alert"` error, strip reverted; offline (`Object.defineProperty(navigator,'onLine',…)` + `offline` event) → Language strip disabled + "Needs a connection", Appearance unaffected. Do **not** re-test PreferenceStrip internals.
  - [x] `settings-page.test.tsx` — **all existing tests pass unchanged** (jsdom → `useWideBreakpoint()` is `false`, so the card renders in every existing test; `LanguageRow` renders safely with the `locale: null` context default; no existing test counts radios/headings that the card changes — verify by running the file before adding anything). Add: (a) narrow (jsdom default): "Account" heading (level 2) present, Preferences card present, **DOM order Account heading → card → Log off button**, Log off still opens the same confirm dialog; (b) wide: stub `window.matchMedia` so `(min-width: 660px)` matches (copy the idiom in `use-wide-breakpoint.test.ts`) → `queryByTestId('preferences-card')` is `null`, no `Language`/`Appearance` radiogroup in the tree, Log off button still in the DOM (it's CSS-hidden, not JS-gated — unchanged behavior); (c) crossing the breakpoint (fire the `matchMedia` `change` listener) unmounts the card without errors, including when a Language save is pending (LanguageRow's unmount guard + `setLocale` still applied — the announcement is allowed to be dropped since its live region unmounted; note it).
  - [x] Persistence across widths (AC #4) at the component level: render `App` (or a minimal harness with a real `HouseholdLocaleContext` provider + `ProfileMenu`/`NavChrome`) → change Language in the card → the context locale changed; then open the Profile menu and assert Language strip's checked segment and (after `setThemePreference('dark')` through the card) Appearance's checked segment match. Prefer extending `App.test.tsx` if a Settings-view harness already exists there (`grep -n "settings" web/src/App.test.tsx`); otherwise a small harness in `preferences-card.test.tsx`.
  - [x] `section-label.test.tsx` only if `SectionLabel` gains the narrow-only variant.

- [x] **Task 6: Playwright (AC #1-#4)** — extend `web/e2e/app-shell.spec.ts` (do **not** create a new spec file); reuse the faked `**/api/session` pattern (see the Story 8.11 test near line ~804: `locale: 'de-DE'`, `email` set) and `page.route('**/api/households/*/locale', …)` → 200
  - [x] At 500px → open Settings (`getByRole('button', { name: 'Settings' }).first()`): "Account" heading visible, Preferences card visible above Log off (compare `boundingBox().y`); at 659px still visible; at 660px the card and "Account" heading are **not in the DOM** (`toHaveCount(0)`) and the top-nav Account menu is reachable.
  - [x] AC #3: for each of 320px and 340px × (`en-US`, `de-DE`) × (online, `context.setOffline(true)`): for each `[data-testid="preferences-card"]` row, assert `scrollWidth <= clientWidth` on the card and the document (no horizontal scroll: `document.documentElement.scrollWidth <= window.innerWidth`), each sub-label's bounding-box height is single-line (≤ ~1.4× its computed `font-size`), and each radio's bounding box width ≥ 44. Hit-area height (44 = 40 + `::before`) can't be read from `boundingBox()` — assert the 40px visual + presence of the `before:` inset via computed style, and prove the 44×44 live in Task 7.
  - [x] AC #4: at 500px in `de-DE` choose `Dunkel` (Appearance) and `English` in the card (route fulfils 200; `page.evaluate(() => document.documentElement.lang)` → `en-US`; heading/labels flip); then `page.setViewportSize({ width: 1000, height: 800 })`, open "Account menu" → Appearance `Dark` checked, Language `English` checked. A 500 response variant → strip reverts + alert visible in the still-persisted language. Use `exact: true` for role names that are substrings of others ("System — currently Dark").
  - [x] Playwright housekeeping (Stories 8.6–8.11 Debug Logs): move `certs/vite-dev-cert.pem`/`.key` aside before running and restore immediately; check for an orphaned `vite preview` on port 4173.

- [x] **Task 7: Full regression + live Chrome verification (mandatory — do not defer)**
  - [x] `npm --prefix web run test`, `tsc -b`, `oxlint` (no new warnings), `npx playwright test`; `dotnet test` is **not** required for code (no backend change) but run `EnergyTracker.Architecture.Tests` (6/6 — this story adds no `localStorage`/`sessionStorage` use; the `FrontendDoesNotStoreAuthTokensTests` allowlist must stay exactly as it is).
  - [x] **This story's new surface writes persisted household state (Language) from a new entry point and is browser-dependent (responsive breakpoint swap, touch-size hit areas at 320–340px, live language switch, `<html lang>`, live region). Same gate as Stories 8.1–8.11: it cannot move review→done without a live Claude-in-Chrome verification actually performed.** If Chrome isn't connected, raise it immediately and pause for the user to connect it; do not defer or tick the box on unit/e2e proof alone (`project-context.md` Process gates).
  - [x] Verify live against the local stack (`scripts/run-api.sh` + Vite at https://localhost:5173; OIDC test-user flow in `docs/local-development.md`). Open a **new tab at the target width** — `resize_window` on an existing tab is unreliable (Stories 8.6/8.7/8.10/8.11): at ~340px **and** ~320px, **Light and Dark**, **de and en**: Settings → "Konto"/"Account" label, Preferences card above Log off, rows on one line, no horizontal scroll; segments measure 44×44 (40px + 2×3px `::before`); toggle Appearance (instant) and Language (spinner → switch without reload; live region text; `<html lang>`); offline (DevTools) → Language disabled + "Needs a connection"/"Benötigt eine Verbindung" fits on one line, Theme still switchable; keyboard: Tab order Appearance → Language → Log off, ←/→/Home/End move-and-select in each strip (no Radix menu here, so native Tab flow — the Profile menu's `cycleTabStops` must **not** be applied); then widen a separate tab/window to ≥660px (or open a second tab) → Account menu shows the same Theme and Language; Card and "Account" label absent at ≥660px.
  - [x] **This story writes the household Locale on the real test household: restore the original Locale (`de-DE`) when done and say so in Completion Notes.** Also reset the device theme preference to System. Do not modify readings, tariffs, or any other data.

- [x] **Task 8: Housekeeping + verify against every AC (AC #1-#5)**
  - [x] `deferred-work.md`: mark the 8.10 "`PreferenceRow` nowrap overflow" item **Resolved by Story 8.12** (copy how 8.11 annotated its resolved items in the same section — grep `Resolved by Story 8.11` first). The Radix-menu-radiogroup ARIA-semantics item stays open (it concerns the Profile menu, not this card — note that the card is plain-page context with correct radiogroup semantics). Do not close the inline-script drift-test item.
  - [x] Walk AC #1-#5 individually in Completion Notes and state what proves each (unit test, e2e assertion, or live check) — same AC-by-AC accounting as Stories 8.1–8.11.

## Dev Notes

### Scope

**Frontend only.** New: `PreferencesCard` component (+ test) and one catalog key. Modified: `SettingsPage` (Account group), possibly `PreferenceRow`/`AppearanceRow`/`LanguageRow` (optional `className` passthrough, padding fit), both locale JSON files, `e2e/app-shell.spec.ts`, `deferred-work.md`. **No backend, no migration, no new endpoint, no new `localStorage` use, no new dependency, no new design tokens.** Stories 8.10 and 8.11 (both `done`, merged: `b147dd8`, `a249d71`) already built everything behavioral — `PreferenceStrip`, `PreferenceRow`, `AppearanceRow`, `LanguageRow`, `HouseholdLocaleContext`, `updateHouseholdLocale`, `useOnlineStatus`, the `<html lang>` sync and the App-level `changeLanguage` effect. 8.11 explicitly built `LanguageRow` "so 8.12 can mount it unchanged (it reads everything from props/context, nothing menu-specific)". This story is composition + layout + fit, not new behavior. **If you find yourself re-implementing saving/revert/offline logic, stop — it exists.**

### Read-before-modify current state (Step 3 analysis; files marked UPDATE)

- `web/src/components/settings/settings-page.tsx` — **UPDATE.** `SettingsPage({ householdId, supportsFederatedLogout, email, onBack, onTrendHistoryClick, onTariffRadarClick })`; layout is `main.flex.flex-col.gap-6.p-4` → `div[data-slot="settings-content"]` (wide: `mx-auto w-full max-w-[900px]`) → header row → `flex flex-col gap-[var(--spacing-card-gap)]` stack of four `wide:order-N` blocks (Yearly Baseline; AI Plausibility + Household pair; Rooms/Power Points/Devices; Data) → **bare `Button variant="outline" className="wide:hidden self-start"` Log off** (no group/label today) → logoff `Dialog` (three `logoffStep`s) → `NavChrome active="settings"`. Preserve: every `wide:order-*` wrapper, the dual-render paths (`InviteMemberRow`), `useLogoff`, the `Dialog`, `NavChrome`. `SettingsPage` receives **no** locale/household object (only `householdId`) — `LanguageRow` needs only `householdId` plus context, and `App.tsx` already wraps the whole ready tree in `HouseholdLocaleContext.Provider` (`App.tsx:389`), so **no `App.tsx` change** is needed and `SettingsPage`'s props stay the same (existing test `renderSettingsPage` must still compile unchanged).
- `web/src/components/settings/section-label.tsx` — `<h2 className="hidden wide:block …">` **(visible ≥660px only)**. This is the trap: the obvious `<SectionLabel>{t('…account')}</SectionLabel>` would render the Account label at exactly the width where the card does *not* exist, and hide it where it should show. See Task 2.
- `web/src/components/preferences/{preference-row,appearance-row,language-row,preference-strip}.tsx` — read in full. `PreferenceRow` = `flex min-h-12 items-center justify-between gap-3 px-2 py-1` + `whitespace-nowrap` on label (12.5px/600) and sub-label (11px); accepts `className`. `AppearanceRow` takes **no props**; `LanguageRow` takes `householdId` and optional `onAnnounce`; both own a `useId()` label id. `PreferenceStrip` segments: 44×40 + `before:` 44×44 hit area, `motion-safe:animate-spin`, pending/disabled/roving-tabindex all done in 8.11. **Do not modify `PreferenceStrip`.**
- `web/src/components/dashboard/profile-menu.tsx` — reference only (live region idiom at lines 68-76; `cycleTabStops` is Radix-menu-specific and must **not** be copied into the card). Not modified.
- `web/src/hooks/use-wide-breakpoint.ts` — `useSyncExternalStore` over `matchMedia('(min-width: 660px)')`, `false` in jsdom and on the server snapshot; existing precedent for "dual layout in JS" elsewhere (`TrendHistoryPage` table/grid). Comment says it must match `--breakpoint-wide` (`web/src/index.css:10`) — don't introduce a second literal.
- `web/src/lib/household-locale-context.ts` — `{ locale, setLocale }`; default `{ null, noop }`.
- `web/src/components/settings/settings-page.test.tsx` — 279 lines; renders `SettingsPage` with **no** `HouseholdLocaleContext` provider and a fetch stub that returns `jsonResponse(null)` for unknown URLs. The card will render in these tests (jsdom narrow) with a `null` locale; that's the designed-safe path — confirm no pre-existing assertion counts `radiogroup`/`h2`/`radio`s.
- `web/e2e/app-shell.spec.ts` — has the Settings breakpoint test (~line 542) and the 8.10/8.11 preference tests (~line 804+) with the session-faking and `exact: true` idioms to reuse.

### What must be preserved (system must work end-to-end)

- Log off on Settings (<660px) is the **only** logoff entry point below 660px (`EXPERIENCE.md`): same button, same handler, same dialog steps, still `wide:hidden`. Every existing `Logoff control (Story 1.12, FR-33)` test passes untouched.
- The Profile menu (≥660px) is untouched: no change to 296px width, row order, `cycleTabStops`, or live region. Passing `className` through the rows must not alter ProfileMenu output.
- Theme stays per device and offline-capable (`lib/color-scheme.ts`, `useThemePreference`); Language stays per Household and offline-disabled. The card adds a second **entry point**, not a second state store: both widths read the same `localStorage` value and the same `HouseholdLocaleContext`. That is what makes AC #4 true by construction — add a test, not new plumbing.
- **Card unmount mid-save**: when the viewport crosses 660px while a Language PUT is in flight, `LanguageRow`'s existing guards (mounted ref, `requestIdRef`) apply; `setLocale` still runs (context lives in `App`). The polite announcement is lost because its live region unmounted with the card — acceptable, note it; do not hoist the live region to `SettingsPage`.

### Design spec (verbatim sources)

- Mockup `_bmad-artifacts/planning/ux-designs/ux-energy-tracker-2026-08-08/mockups/key-profile-preferences.html` **§3** (340px phone, Dark `de-DE` + Light `en-US`): "Konto"/"Account" section label → glass `.card` (radius 18px, padding 6px 10px, hairline between rows) holding the two `.pref-row`s → a separate `.settings-row` Log off (unchanged control, rendered there as a card-like row — **the implemented Log off is the existing outline `Button`; do not restyle it**, AC #5). Caption: "Scope sub-lines are kept to two or three words so they never wrap at 320–340px."
- `DESIGN/components.md` → **Preference icon strip** (strip/segment metrics; "used twice in the Profile menu (≥660px) and twice in Settings' Preferences card (<660px)"). UX-DR33 in `requirements-inventory.md`. `EXPERIENCE.md` line 40 (Preferences paragraph) and Information Architecture row.
- "SPINES WIN": if the mock and `EXPERIENCE.md` / `DESIGN/components.md` disagree, the spines win (mockup footer).

### Architecture compliance

- **AD-18:** strings in both catalogs; `Household.Locale` remains the single field driving language + formatting — this story adds a second caller of the existing 8.11 write path, no new write path.
- **AD-3 / AD-15 / AD-1:** untouched (no backend).
- **Security (project-context):** no `localStorage`/`sessionStorage` literal added in `web/src` — `FrontendDoesNotStoreAuthTokensTests` stays green with its allowlist **unchanged** (theme key lives only in `lib/color-scheme.ts`).
- **Frontend conventions:** `import type`; no enums/param properties (`erasableSyntaxOnly`); `@/` alias; oxlint (not ESLint); new feature UI under `components/preferences/` (not `components/ui`); no `.eslintrc`; `react/only-export-components` is a **warn** — keep `PreferencesCard` the file's only component export.
- **No new tokens / no new dependency** (shadcn `Card` only if the sibling settings cards already use it — check, don't `npx shadcn add`).

### Previous story intelligence (8.10, 8.11)

- 8.11 review/live-verification lessons that apply here: live checks found real bugs unit tests missed (Tab unreachable, 42px hit area in 8.10) — expect layout/fit surprises at 320px and do the live pass before claiming done. Use a **new correctly-sized tab**, not `resize_window`. `exact: true` in Playwright role/name locators. Certs aside for Playwright; check for an orphaned `vite preview` on port 4173.
- 8.11 live notes: the account signed in during live verification is not necessarily the OIDC test user; "true network-offline" was simulated via `navigator.onLine` + `offline`/`online` events — for this story prefer DevTools/`context.setOffline(true)` in Playwright and say which method was used.
- 8.11 tests reset `i18next` to `en-US` in `afterEach` (`web/src/test/setup.ts` imports `@/i18n` globally) and reset theme (`setThemePreference('system')`) — do both in every new test that changes them or unrelated suites' English/theme assertions break.
- 8.11 Completion Notes: two existing 8.10 tests were edited because they encoded a single-strip menu. **For 8.12 the expectation is zero edits to existing tests** — the card is additive and lives outside the Profile menu. If an existing test must change, say exactly why in Completion Notes.
- `deferred-work.md` items **this story resolves**: `PreferenceRow` nowrap overflow (Task 8). Items it **leaves**: Radix-menu/radiogroup ARIA semantics (Profile-menu-specific), inline-script/`color-scheme.ts` drift test.
- Process gate (project-context): any post-merge fix to this story needs a linked spec/story doc.

### Git intelligence

`fe1b93b` (doc: mark 8.11 done) ← `a249d71` Story 8.11 (#83) ← `b147dd8` Story 8.10 (#82) ← `6fba2b1` (planning for theme/language toggle) ← `3fdee3d` 8.9 (#81). Story PRs are single-squash commits touching `web/src/components/*`, locale catalogs, `web/e2e/app-shell.spec.ts`, and the story/sprint-status files; 8.10/8.11 created everything under `components/preferences/`. Commit message style: `Story 8.12: Preferences card in Settings below 660px (UX-DR33, FR-34, FR-35) (#NN)`. Current branch `feature/8-12-settings-preferences-mobile` is already created off `main`.

### Testing standards summary

- jsdom has no layout or media queries → `useWideBreakpoint()` is `false` (narrow) unless `matchMedia` is stubbed; geometry (no wrap/overflow, 44×44 hit area, horizontal scroll) is provable only in Playwright/live. Assert structure/order in Vitest.
- Vitest + Testing Library, colocated tests, `globals` on, `jsdom`. `navigator.onLine` via `Object.defineProperty(navigator, 'onLine', { configurable: true, value })`, restore in `afterEach`.
- `settings-page.test.tsx` dual-render convention: several Settings elements render in both narrow and wide variants (jsdom applies no `hidden`/`wide:hidden`), so some existing queries use `findAllBy…[0]`. The card is JS-gated so it renders **once** in narrow tests; the new "Account" `<h2>` is `wide:hidden` and also appears once.
- Frontend E2E in `web/e2e/app-shell.spec.ts` (Playwright `test:e2e`); .NET tests not applicable except the Architecture guard run.

### Project Structure Notes

- New: `web/src/components/preferences/preferences-card.tsx` (+ `preferences-card.test.tsx`).
- Modified: `web/src/components/settings/settings-page.tsx` (+ tests, + `section-label.tsx`/test only if the narrow-only variant is chosen), `web/src/components/preferences/{preference-row,appearance-row,language-row}.tsx` (only if `className` passthrough/padding is needed), `web/src/locales/{en-US,de-DE}/translation.json`, `web/e2e/app-shell.spec.ts`, `_bmad-artifacts/implementation/deferred-work.md`, `sprint-status.yaml`, this story file.
- Variances to record: (1) JS-gated render via `useWideBreakpoint()` in addition to the CSS `wide:hidden` wrapper (AC says "not rendered"); (2) visible group label is "Account"/"Konto" while the AC/UX name the card "Preferences" (mockup §3 shows no separate "Preferences" text); (3) Log off keeps its existing outline `Button` styling rather than the mockup's card-like `.settings-row` (AC #5 "unchanged").

### References

- [Source: _bmad-artifacts/planning/epics/epic-8-desktop-tablet-layout-correction.md#Story 8.12]
- [Source: _bmad-artifacts/planning/epics/requirements-inventory.md — UX-DR33, UX-DR32, FR-34, FR-35]
- [Source: _bmad-artifacts/planning/ux-designs/ux-energy-tracker-2026-08-08/mockups/key-profile-preferences.html §3, §4]
- [Source: _bmad-artifacts/planning/ux-designs/ux-energy-tracker-2026-08-08/DESIGN/components.md — Preference icon strip; EXPERIENCE.md — Profile menu/Preferences paragraph]
- [Source: _bmad-artifacts/implementation/8-10-theme-toggle-profile-menu.md, 8-11-language-toggle-household-locale-switch.md — strip/row/context components, live-verification notes]
- [Source: _bmad-artifacts/implementation/deferred-work.md — "Deferred from: code review of 8-10…" (PreferenceRow nowrap overflow → Story 8.12)]
- [Source: _bmad-artifacts/project-context.md — Frontend/Testing rules, Process gates (live Chrome verification)]
- [Source: web/src/components/settings/settings-page.tsx, section-label.tsx; web/src/components/preferences/*; web/src/components/dashboard/profile-menu.tsx; web/src/hooks/use-wide-breakpoint.ts; web/src/lib/household-locale-context.ts; web/src/App.tsx]

## Dev Agent Record

### Agent Model Used

Claude Sonnet 5.5

### Debug Log References

- `npm run preview` serves the built `dist`; Playwright against a stale build showed no card. Rebuilt (`npm run build`) before the e2e run. Certs moved aside and restored; no orphaned preview on 4173.

### Completion Notes List

- `PreferencesCard` (GlassCard like sibling settings cards; hairline divider; own `sr-only` live region fed by `LanguageRow.onAnnounce`). `AppearanceRow`/`LanguageRow` gained an optional `rowClassName` passthrough (default unchanged → ProfileMenu output identical); the card passes `px-0`.
- `SettingsPage`: new `wide:hidden` Account group (`h2` "Account"/"Konto", JS-gated card via `useWideBreakpoint()`, unchanged Log off Button). `SectionLabel` untouched (new inline `h2`).
- Fit: fix (1) `px-0` alone sufficed at 320 and 340px in en-US/de-DE, online and offline; no `gap`/truncate needed.
- Catalogs: added `settings.sections.account` only; scripted key diff of en-US vs de-DE = empty.
- Zero edits to existing tests. Unmounting the card mid-save: `setLocale` still applied, announcement dropped (live region unmounts with the card) — covered by a unit test.
- Results: vitest 516 pass, `tsc -b` clean, oxlint no new warnings, Playwright 24/24, Architecture tests 6/6.
- AC proof: #1 settings-page test (order/heading/card) + e2e (y-order); #2 unit (matchMedia stub) + e2e (660px `toHaveCount(0)`); #3 e2e 320/340 × en/de × online/offline (no overflow, single-line, radio width ≥44); #4 unit harness with ProfileMenu + e2e widen to 1000px; #5 existing tests unchanged, key parity diff.
- **Task 7 live Claude-in-Chrome verification performed** against the local stack (Postgres + `run-api.sh` + Vite, already-signed-in session, household `de-DE`). Method caveat: `resize_window`/new-tab sizing is unreliable, so I drove a same-origin iframe at 340px / 320px / 700px (real media queries at the iframe width). Verified: "Konto"/"Account" label → Preferences card → Log off order; rows and sub-labels single-line (15.7px) with no horizontal scroll at 340 and 320px in de and en, Light and Dark; segments 44×40 with a −3px `::before` inset top/bottom (46px tall hit area, ≥44); Theme toggles instantly; Language switches without reload (`<html lang>` → `en-US`, live region "Language: English", heading → "Account"); offline (simulated via `navigator.onLine` + `offline`/`online` events, not DevTools) → Language disabled, "Needs a connection" on one line, Theme still switchable; arrow keys move-and-select in a strip; Tab order Appearance → Language → Log off; freshly loaded at 700px the card is absent (0 radiogroups outside the menu) and the Account group is `display:none`, while the Account menu shows the same Theme (Light) and Language (English). Limitation: media-query `change` events don't fire inside the iframe in this automation browser (my own listener got none), so live breakpoint-crossing was not observable; that path is covered by the Vitest matchMedia test and Playwright `setViewportSize`.
- **Variances recorded (code review 2026-09-30):** (1) the Account card is JS-gated (`!wide`) *and* its wrapper is CSS `wide:hidden` — belt-and-braces; the "Account" `h2`/Log off stay in the DOM as `display:none` at ≥660px. (2) The section label reads "Account"/"Konto" while the card is named "Preferences" in the story/mockup. (3) Log off keeps Story 1.12's outline Button rather than the mockup's card-like row (AC #5: unchanged). Live verification ran in an iframe rather than a real-width tab; Ralf accepted that evidence in review.
- Restored afterwards: household Locale back to `de-DE`, device theme reset to System. No other data touched.

### File List

- web/src/components/preferences/preferences-card.tsx (new)
- web/src/components/preferences/preferences-card.test.tsx (new)
- web/src/components/preferences/appearance-row.tsx
- web/src/components/preferences/language-row.tsx
- web/src/components/settings/settings-page.tsx
- web/src/components/settings/settings-page.test.tsx
- web/src/locales/en-US/translation.json
- web/src/locales/de-DE/translation.json
- web/e2e/app-shell.spec.ts
- _bmad-artifacts/implementation/deferred-work.md
- _bmad-artifacts/implementation/sprint-status.yaml
- _bmad-artifacts/implementation/8-12-settings-preferences-card-mobile.md

### Change Log

- 2026-09-30: Story 8.12 implemented — Preferences card in Settings below 660px.

### Review Findings

Code review 2026-09-30 (Blind Hunter, Edge Case Hunter, Acceptance Auditor; 12 dismissed as noise).

- [x] [Review][Decision] Resolved 2026-09-30: Ralf accepted the iframe evidence (not redone in a real tab). Live verification deviated from Task 7 — Completion Notes say checks ran in a same-origin iframe (340/320/700px) with offline simulated via `navigator.onLine`; the spec asks for a new tab at target width and DevTools offline (lessons from 8.6/8.7/8.10/8.11). Media-query `change` events did not fire in the iframe, so live breakpoint crossing wasn't observed, and Home/End in each strip isn't recorded. Per the live-check gate this blocks review→done: accept the iframe evidence, or redo the live pass in a real tab?
- [x] [Review][Patch] e2e label-overflow probe cannot fail — `scrollWidth > clientWidth` on the inline label `<span>` is always 0/0; `labelH <= 1.6×font` (spec says ~1.4×) can't catch overflow of a `whitespace-nowrap` label. Measure the row/label bounding boxes against the strip and card instead, and use the spec's 1.4× [web/e2e/app-shell.spec.ts]
- [x] [Review][Patch] Hit-area assertion incomplete — only `boundingBox().width >= 44`; Task 6 also requires the 40px visual height and the `before:` inset via computed style [web/e2e/app-shell.spec.ts]
- [x] [Review][Patch] AC #4 "or reload" untested and persist e2e is weak — add a reload step, and assert the theme `localStorage` write and the locale PUT actually happened [web/e2e/app-shell.spec.ts]
- [x] [Review][Patch] No assertion that Log off still opens its confirm dialog inside the new Account wrapper [web/src/components/settings/settings-page.test.tsx]
- [x] [Review][Patch] Test robustness — `setOffline(false)` not in `try/finally`; `boundingBox()!` and `resolvePut` used without a clear precondition assertion; `html.dark` cleanup skipped on failed assertion (move to `afterEach`) [web/e2e/app-shell.spec.ts, web/src/components/settings/settings-page.test.tsx, web/src/components/preferences/preferences-card.test.tsx]
- [x] [Review][Patch] Completion Notes omit the three "Variances to record" from Dev Notes (JS-gate + CSS belt-and-braces; "Account" label vs "Preferences" name; outline Log off vs mockup's card-like row) [8-12-settings-preferences-card-mobile.md]
- [x] [Review][Defer] Language announcement and error state are lost if the viewport crosses 660px mid-save (card unmounts) [web/src/components/preferences/preferences-card.tsx] — deferred, rare resize-mid-request edge; fix would lift the live region above the `!wide` gate
- [x] [Review][Defer] 660px breakpoint lives in three places (CSS `--breakpoint-wide`, `WIDE_QUERY`, e2e) with no enforced sync [web/src/hooks/use-wide-breakpoint.ts] — deferred, pre-existing (hook predates 8.12)

- 2026-09-30: Code review — 6 patches applied (stricter e2e geometry/hit-area probes, reload + PUT/localStorage persistence checks, Log off dialog assertion, test hygiene, variances recorded); decision accepted; 2 deferred. Status → done.
