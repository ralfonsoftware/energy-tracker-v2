---
baseline_commit: 6fba2b1
---

# Story 8.10: Theme Toggle in the Profile Menu (System / Light / Dark)

Status: done

<!-- Note: Validation is optional. Run validate-create-story for quality check before dev-story. -->

## Story

As a Household member,
I want to choose System, Light, or Dark from the Profile menu,
so that I can override my device's color scheme when I prefer the other theme, without changing my OS setting.

## Acceptance Criteria

1. **Given** the Profile menu is open (≥660px, Story 8.1), **when** rendered, **then** it shows, between the account email and the Profile / Log off rows, an "Appearance" row carrying the new Preference icon strip (UX-DR32) with exactly three segments — System, Light, Dark (monitor / sun / moon icons) — plus the scope sub-label "This device", with the dropdown widened to 296px; the active segment shows the fill, 1px accent border, and `aria-checked="true"` (never color alone), using the existing nav-chrome active tokens and the canonical focus-ring pair.
2. **Given** the strip has keyboard focus, **when** the member presses ←/→ (or ↑/↓) or Home/End, **then** focus moves **and selects** per `radiogroup` semantics, Tab enters the group on the checked segment (inside the Profile menu, Radix traps Tab, so Tab/Shift+Tab cycle between each strip's checked segment and the first enabled menu item; other items are reached with arrow keys), every segment has an accessible name (the System name includes the resolved value, e.g. "System — currently Dark"), and Escape closes the menu returning focus to the avatar button.
3. **Given** the member selects Light or Dark, **when** the selection is made, **then** the theme applies immediately on every screen without reload or network request, the menu stays open, and the choice is stored per device in `localStorage` — not on the Household, not per member (FR-35).
4. **Given** System is selected (the default when nothing is stored), **when** the OS color-scheme preference changes, **then** the app follows it live (today's `initColorScheme` behavior is preserved); with Light or Dark selected, OS changes are ignored.
5. **Given** a stored choice exists, **when** the page loads or reloads (including after logoff), **then** the stored theme is applied before first paint — the inline script in `web/index.html` reads it, so there is no flash of the wrong theme — and the `<meta name="theme-color">` values follow the effective theme rather than only the OS media query.
6. **Given** `localStorage` is unavailable or holds an unrecognized value, **when** the app loads, **then** it falls back to System without error.
7. **Given** the device is offline, **when** the member changes the theme, **then** it works exactly as online (no server involvement).
8. **And** all new strings exist in both `en-US` and `de-DE` catalogs (AD-18); Dark and Light both remain fully designed on every surface (UX-DR11), verified in both themes for the new row (UX-DR32, FR-35).

## Tasks / Subtasks

- [x] **Task 1: Resolve the `localStorage` architecture guard FIRST (AC #3, #5, #6)** — `tests/EnergyTracker.Architecture.Tests/FrontendDoesNotStoreAuthTokensTests.cs`, `project-context.md`
  - [x] **This is a hidden blocker.** `Frontend_source_never_reads_or_writes_localStorage_or_sessionStorage` scans every `.ts`/`.tsx` under `web/src` (tests included) and fails on the literal string `localStorage` or `sessionStorage`. FR-35 mandates `localStorage`, so the theme code (`color-scheme.ts`) **and its test** would fail the .NET suite. The guard exists to stop token caching (AD-17), not to ban a theme preference.
  - [x] Amend the guard with a **narrow, explicit allowlist** — exactly `lib/color-scheme.ts` and `lib/color-scheme.test.ts` (compare by path suffix, normalize `\`/`/`) — with a comment stating why (device-local theme preference, FR-35; not an auth token). Keep the scan for every other file. Add an assertion so the allowlist can't silently rot: each allowlisted file must exist (fail if renamed without updating the guard).
  - [x] Do **not** work around the guard with string tricks (`window['local' + 'Storage']`, a helper returning the storage object via `globalThis`, etc.) — that defeats the invariant's intent and is exactly what a reviewer will reject.
  - [x] The stored key is a plain literal (recommend `energy-tracker-theme`, values `'system' | 'light' | 'dark'`). It must never hold anything token-like; note this in the guard comment.
  - [x] Update `project-context.md` → Security → "Never store an auth token client-side (`localStorage`/`sessionStorage`/…)" with one clause: the sole, allowlisted exception is the theme preference in `lib/color-scheme.ts` (FR-35). `web/index.html` is not scanned by the guard (only `.ts`/`.tsx`), so the inline script may reference `localStorage` freely.

- [x] **Task 2: Extend `web/src/lib/color-scheme.ts` — do not add a parallel mechanism (AC #3-#7)**
  - [x] Keep `initColorScheme()` and its call in `src/main.tsx:8` (do not move it). Add: `type ThemePreference = 'system' | 'light' | 'dark'`, `getThemePreference()`, `setThemePreference(pref)`, `getResolvedTheme(): 'light' | 'dark'`, and a `subscribeTheme(cb)` + snapshot pair so React can bind with `useSyncExternalStore` (same idiom as `hooks/use-wide-breakpoint.ts`). Put the React hook in `web/src/hooks/use-theme-preference.ts` (returns `{ preference, resolved, setPreference }`).
  - [x] `apply()` toggles `document.documentElement.classList.toggle('dark', resolved === 'dark')` where `resolved` = the stored pref if `light`/`dark`, else the OS `matchMedia('(prefers-color-scheme: dark)').matches`. The existing OS `change` listener stays and re-applies **only when the current preference is `system`** (AC #4) — read the preference at event time, not closed over at init.
  - [x] `setThemePreference` writes storage (try/catch — Safari private mode / disabled storage throws on access **and** on `setItem`; in-memory fallback so the choice still applies for the session), calls `apply()`, updates the `<meta name="theme-color">`, and notifies subscribers. `system` → `removeItem` (or store `'system'`; pick one and test it — recommend `removeItem` so "nothing stored" ≡ System, AC #6).
  - [x] Reading: any value not in `{'light','dark','system'}` → `'system'`; wrap `getItem` in try/catch (AC #6). Never throw.
  - [x] Also listen to the `storage` event (cross-tab): another tab on the same device changing the theme should re-apply here — cheap, and it's the correct "per device" semantics. Ignore events for other keys.
  - [x] **Update the header comment** ("No manual toggle yet…" is now false). State: preference is System/Light/Dark, per device, System = OS (live), and that `index.html` holds a synchronous inline copy of the read logic that **must stay in sync** with `getThemePreference`'s key/values.
  - [x] Theme-color meta: see Task 3. `initColorScheme`'s `apply()` and `setThemePreference` both call one small `syncThemeColorMeta(resolved)` helper so first load and later changes cannot diverge. Colors are the two literals already in `index.html` (light `#F3F8ED`, dark `#12201A`) — define them once in `color-scheme.ts` as constants and reference them from the same-valued inline script (duplicated literal in the inline script is unavoidable; comment it).
  - [x] Guard against `window.matchMedia` being undefined (jsdom): the existing test mocks it via `vi.stubGlobal`; keep `initColorScheme()` behavior identical for the existing three tests (they must pass **unchanged**).

- [x] **Task 3: `web/index.html` — pre-paint stored theme + effective theme-color (AC #5, #6)**
  - [x] Today: two `<meta name="theme-color" media="(prefers-color-scheme: …)">` tags and an inline script that only checks the OS. Replace the two media-conditioned metas with **one** `<meta name="theme-color" content="#F3F8ED">` (no `media`) that the inline script sets to the effective theme's color synchronously. Rationale: a `media`-conditioned meta follows the OS regardless of the override, so with Light chosen on a Dark-OS device the browser chrome would stay dark (AC #5 explicitly requires the meta to follow the effective theme).
  - [x] Inline script (synchronous, before CSS): `try { stored = localStorage.getItem('<key>') } catch {}`; `dark = stored === 'dark' || (stored !== 'light' && matchMedia('(prefers-color-scheme: dark)').matches)`; `classList.toggle('dark', dark)`; set the meta `content`. Wrap the whole thing in try/catch so a throwing `localStorage` (AC #6) still falls back to the OS check. Update its comment.
  - [x] No-JS / pre-script fallback: keep a sensible default `content` on the single meta (light). Do not reintroduce a second meta with `media`.
  - [x] Verify there is no flash: hard-reload with Light stored on a Dark-OS emulation (Playwright `colorScheme: 'dark'`) and assert `html` has no `dark` class at `DOMContentLoaded`/first evaluate before React mounts (see Task 7).

- [x] **Task 4: New shared component — Preference icon strip (AC #1, #2, #8)** — new folder `web/src/components/preferences/` (feature grouping per project-context; **not** `components/ui`, **not** `dashboard/`, because Stories 8.11 and 8.12 reuse it from Profile menu and Settings)
  - [x] `preference-strip.tsx`: generic, controlled, presentational — props roughly `{ options: {value, label, icon?|code?}[]; value; onChange(value); ariaLabel/ariaLabelledBy; disabled?; pendingValue?; 'data-testid'? }`. Language (8.11) needs `disabled` + `pendingValue` (saving spinner) — **build those props now but only exercise them minimally** (a unit test each) so 8.11 doesn't re-open the component. No theme- or locale-specific logic inside the strip.
  - [x] Semantics: container `role="radiogroup"`; segments `<button type="button" role="radio" aria-checked>`, roving `tabIndex` (`0` on checked, `-1` on the rest), each with an accessible name (`aria-label`) plus `title` for hover. ←/→ and ↑/↓ move **and select** (wrapping), Home/End jump to first/last and select. Selecting an already-checked segment is a no-op.
  - [x] Visual per `DESIGN/components.md → Preference icon strip` and mockup `.strip`/`.seg` (`key-profile-preferences.html`): container `rounded-xl` (12px), `p-0.5` (2px), `gap-0.5`, 1px hairline border, fill `rgba(220,245,230,0.07)` dark / `rgba(255,255,255,0.6)` light; segments `w-11 h-10` (44×40) with `rounded-[10px]`, 16px icon; **selected** = `bg-nav-chrome-active-bg text-nav-chrome-active-foreground` **plus** 1px border `rgba(111,209,177,0.55)` dark / `rgba(30,122,97,0.55)` light; hover = neutral tint `rgba(220,245,230,0.1)` dark / `rgba(40,70,50,0.07)` light; `focus-visible` = the app's canonical 2px `--ring` outline with 1px offset (`outline-2 outline-ring outline-offset-1` — check how other components in the repo express the focus ring and match them); disabled = `opacity-45` (build now for 8.11); pending = `opacity-65` + small inline spinner. **No new color/radius/type-size tokens** — arbitrary-value classes for the two rgba literals are acceptable and already the established pattern in `glass-classnames.ts`.
  - [x] **44×44 hit area with a 44×40 visual segment (UX-DR32):** the strip's inner padding is 2px, so the row must supply the extra hit height (mockup: "row padding"). Verify the clickable area is ≥44px tall (e.g. `min-h` on the row / the segment's own `::before` hit-slop), don't just assert the class.
  - [x] Icons from `lucide-react` (already the icon library): `Monitor`, `Sun`, `Moon` for the theme strip; a `Palette` (14px) for the Appearance label icon per the mockup row anatomy (`Globe` is 8.11's). Check the installed lucide version exports these names before using them (`Monitor`, `Sun`, `Moon`, `Palette` all exist in current lucide; verify against `node_modules`).
  - [x] `preference-row.tsx` (label icon + label 12.5px/600 over a one-line 11px quiet sub-label, `whitespace-nowrap`, strip right-aligned, `min-h-12`) — shared by both rows and by 8.12's card. Sub-label must not wrap in de-DE ("Dieses Gerät") or en-US ("This device").
  - [x] `appearance-row.tsx`: composes the row + strip + `useThemePreference`; option order System, Light, Dark. Accessible names: `System — currently {{resolved}}` (resolved value localized: "Dark"/"Light", de "Dunkel"/"Hell"), `Light`, `Dark`. The label "Appearance"/de "Darstellung" is the radiogroup's accessible name (`aria-labelledby` on the label element) — not redundant with segment names.
  - [x] The strip's ARIA name uniqueness: 8.11/8.12 render several strips; give each an explicit id/`aria-labelledby`, don't rely on DOM order.

- [x] **Task 5: Integrate into `ProfileMenu` without breaking Radix menu behavior (AC #1, #2)** — `web/src/components/dashboard/profile-menu.tsx`
  - [x] Current state (read it first): `DropdownMenuContent` with `className={cn(GLASS_DROPDOWN_CLASSNAME, 'w-56')}` (224px), optional email `DropdownMenuLabel` + separator, a **disabled** "Profile" `DropdownMenuItem`, and a destructive "Log off" item whose `onSelect` `preventDefault()`s and opens the logoff dialog via `requestAnimationFrame`. Story 8.11 will add the Language row directly below Appearance, so structure the insertion point so 8.11 is a one-line addition.
  - [x] Change `w-56` → `w-[296px]` (UX: "widens from 226 → 296px"). Insert Appearance between the email block and Profile: **email → Appearance → (Language, 8.11) → separator → Profile → Log off** (mockup order; add a separator between the email block and the strips only if the mockup shows one — it shows a hairline under the email only, then a separator before Profile/Log off. Match `key-profile-preferences.html` §1).
  - [x] **Radix `role="menu"` gotchas — the dev must verify each, not assume (this is the riskiest part of the story):**
    1. Radix's Menu content `onKeyDown` **`preventDefault`s Tab**, treats ArrowUp/ArrowDown/Home/End/PageUp/PageDown as "focus first/last *menu item*", and runs **typeahead** on character keys. Keydown events bubbling from a strip segment will therefore steal focus to a menu item unless the strip handles them. In the strip's `onKeyDown`, handle ←/→/↑/↓/Home/End and call `event.stopPropagation()` (+`preventDefault`) so Radix's content handler never sees them; check whether `Tab` needs the same treatment for AC #2's "Tab enters the group on the checked segment" (Radix's FocusScope traps focus inside the content, so native Tab between the strip and the Log off item must still work or, at minimum, the strip must be reachable via arrows from the menu — decide and test what the actual behavior is).
    2. Clicking a plain `<button>`/`role=radio` child inside `DropdownMenuContent` must **not** close the menu (only `DropdownMenuItem` selection closes it) — AC #3 "menu stays open". Confirm; if pointer events on non-item children misbehave, wrap the strip row in `onPointerDown`/`onClick` `stopPropagation` guards rather than reaching for `DropdownMenuItem`.
    3. Escape must still close the menu and return focus to the avatar trigger even when focus is inside a segment (AC #2) — Radix does this by default; assert it in the test (don't `stopPropagation` `Escape`).
    4. **ARIA validity:** a `radiogroup` inside `role="menu"` is not a strictly valid menu child. Put each preference row in a wrapper with `role="none"`/`role="presentation"` (or use `DropdownMenuGroup`) so the menu's children semantics stay coherent, and confirm with the Radix version in `package.json` that `getByRole('radio')` inside the open menu still resolves in Testing Library.
    5. If, after honest verification, Radix's menu machinery cannot host the strip acceptably (Tab or arrow handling can't be made to satisfy AC #2), the **fallback** is to render the Profile menu as a `Popover` (already in `radix-ui`) with explicit buttons for Profile/Log off — but that changes Story 8.1's `menuitem` roles and its tests; only take this path if the above fails, and record the decision + why in Completion Notes. Do not silently drop the Tab/arrow AC.
  - [x] Do not disturb: the avatar trigger's `aria-label={t('profileMenu.avatarLabel')}` ("Account menu"), the email-omitted-when-null behavior, the disabled Profile row, the Log off flow (`useLogoff` hook, `requestAnimationFrame(openLogoffDialog)` — the theme change must **not** close/refocus in a way that breaks the dialog), and the `Dialog` markup below the menu.
  - [x] `ProfileMenu` props stay unchanged (`email`, `householdId`, `supportsFederatedLogout`) — the Appearance row reads the theme from `useThemePreference`, not from props.

- [x] **Task 6: i18n — both catalogs (AC #1, #2, #8)** — `web/src/locales/en-US/translation.json`, `web/src/locales/de-DE/translation.json`
  - [x] Extend the existing `"profileMenu"` block (en line ~289: `avatarLabel`, `profile`) — nest under it or a new top-level `"preferences"` block (the same strings are reused by 8.12's Settings card, so a top-level `"preferences"` block is the better home; decide once, and 8.11/8.12 extend it). Keys needed now: appearance label, scope sub-label, option names (`system`, `light`, `dark`), the `System — currently {{value}}` template, and the localized resolved values (`resolvedLight`/`resolvedDark`).
  - [x] Copy (from the mockup / UX spec, verbatim): en "Appearance" / "This device"; de "Darstellung" / "Dieses Gerät". Options: en System/Light/Dark, de System/Hell/Dunkel. Verify de terms against the existing catalog's tone (informal "du" elsewhere in de copy — the mockup's de error text uses "Dein Haushalt").
  - [x] Add a tiny catalog-parity check if one doesn't exist (grep for an existing i18n key-parity test first; if there is one it will cover this, if not do **not** invent a new test framework — just diff the key sets manually and note it).
  - [x] Language endonyms "Deutsch"/"English" are Story 8.11 — do not add them here.

- [x] **Task 7: Tests (AC #1-#8)** — colocated per project-context; Vitest + Testing Library, oxlint, no `.eslintrc`
  - [x] `color-scheme.test.ts` — **keep the existing 3 tests unchanged**, add: stored `light` on a dark OS → no `dark` class; stored `dark` on a light OS → `dark`; `system`/nothing stored follows OS live (existing behavior); with `light`/`dark` stored, a fired OS `change` is **ignored** (AC #4); switching back to `system` re-syncs to the current OS value; unrecognized stored value → System (AC #6); `localStorage.getItem` **throwing** → System, no throw; `localStorage.setItem` throwing → choice still applies for the session; theme-color meta content follows the effective theme (create the meta in the test DOM); `storage` event from another tab re-applies. Clear `localStorage` and the meta + `dark` class in `afterEach` (the existing one only removes the class).
  - [x] `preference-strip.test.tsx` — roles (`radiogroup`/`radio`), `aria-checked`, roving tabindex (checked = 0, others -1), ←/→/↑/↓ move **and select** with wrap, Home/End, click selects, re-select no-op, `disabled` blocks change, `pendingValue` renders the spinner (for 8.11), every segment has an accessible name.
  - [x] `appearance-row.test.tsx` (or fold into profile-menu tests) — three options in order, System's name includes the resolved value and updates when the OS flips, selecting Light/Dark updates `documentElement.classList` immediately and writes storage, sub-label "This device", no `fetch` is called (AC #3/#7 — stub `fetch` and assert it wasn't called), works with `navigator.onLine === false`.
  - [x] `profile-menu.test.tsx` — **every existing test passes unchanged** (email/Profile/Log off rows, disabled Profile, email omitted, Log off drives the shared hook). Add: Appearance row present between the email and Profile rows (DOM order), selecting a segment **keeps the menu open** (`Profile`/`Log off` still in the document), Escape closes and focus returns to the "Account menu" button, arrow keys inside the strip change selection without moving focus to a menu item (the Radix-steal regression), the dropdown carries the 296px width class. The file's `beforeEach` already stubs `hasPointerCapture`/`scrollIntoView` for Radix in jsdom — reuse, don't duplicate.
  - [x] Architecture guard: run `dotnet test tests/EnergyTracker.Architecture.Tests` and confirm the amended guard passes with the allowlist and **still fails** if a new file (temporarily add one) references `localStorage` — prove the allowlist is narrow. No other .NET change; no backend/API/migration work in this story.
  - [x] Playwright (`web/e2e/app-shell.spec.ts`) — extend, don't add a new spec file, unless the existing file structure clearly warrants it. At 1000px, open Account menu → click Dark → `html.dark` present, menu still open; reload → still dark **with no flash** (assert `html` class at `page.addInitScript`-independent first paint: e.g. `page.evaluate` immediately after `waitUntil: 'commit'`/`domcontentloaded` before hydration, using `page.emulateMedia({ colorScheme: 'light' })` to prove the stored choice beats the OS); choose System with `emulateMedia` flipped → follows live; `meta[name=theme-color]` `content` equals the effective theme's color. Storage-unavailable path is covered by unit tests, not e2e. Check whether the existing e2e mocks `/api/session` and how the top-nav test at `app-shell.spec.ts:68` reaches the menu; copy its idiom.

- [x] **Task 8: Full regression + live Chrome verification (mandatory — do not defer)**
  - [x] `npm --prefix web run test`, `tsc -b`, `oxlint` clean (no new warnings); `dotnet test tests/EnergyTracker.Architecture.Tests`; `npx playwright test` (move `certs/vite-dev-cert.pem`/`.key` aside first if present and restore immediately; check for an orphaned `vite preview` on port 4173 — see the Story 8.6–8.9 Debug Logs).
  - [x] **This is browser-dependent behavior (pre-paint script, live OS-follow, `<meta theme-color>`, keyboard/focus in a Radix menu). Same gate as Stories 8.1–8.9: it cannot move review→done without a live Claude-in-Chrome verification actually performed in this session.** If Chrome isn't connected, raise it immediately and pause for the user to connect it; do not defer or tick the box on unit/e2e proof alone (`project-context.md` Process gates).
  - [x] Verify live (real household, Auth0 login, https://localhost:5173, tab opened at ≥660px — `resize_window` on an already-navigated tab is unreliable per Stories 8.6/8.7; open a **new** tab already sized): open Account menu, both themes render the new row against `mockups/key-profile-preferences.html` §1 (Dark + Light, de + en); click each segment (theme flips immediately on Dashboard, Trend History, Tariff Radar, Settings, menu stays open); reload → no flash of the wrong theme; log off and back in → choice survives; System follows a real OS appearance flip (ask Ralf to toggle macOS appearance — do not skip); Light chosen + OS Dark → `theme-color` meta content is the light color; keyboard: Tab/←/→/Home/End/Esc behave per AC #2 in the real Radix menu; segment hit area ≥44×44.
  - [x] **Do not touch real data** — this story writes nothing to the server.

- [x] **Task 9: Verify against every AC**
  - [x] Walk AC #1-#8 individually in Completion Notes and state what proves each (code reference, unit test, e2e assertion, or live check) — same AC-by-AC accounting as Stories 8.1–8.9.

## Dev Notes

- **Scope: frontend + one architecture-guard test + `project-context.md` clause. No backend endpoint, no migration, no DTO, no `Household` field.** FR-35 is deliberately per-device (`localStorage`), unlike FR-34 (Story 8.11), which is the household-scoped one. Do not add any API call for theme, and do not persist theme to the Household "for consistency".

- **Source of truth:** epic `epics/epic-8-desktop-tablet-layout-correction.md` § Story 8.10 (lines ~214-257), PRD `prd/4-features.md` § FR-35, `requirements-inventory.md` UX-DR32 (line ~142), `DESIGN/components.md` (Profile menu ~line 82; **Preference icon strip** ~line 84-88), `EXPERIENCE.md` (Profile menu ~line 40; nav table ~line 102), and the mockup `mockups/key-profile-preferences.html` §1 (menu open, Dark + Light), §2 (strip states: default/hover/focus), §4 (behavior spec). **The spines win if the mockup disagrees.** Stories 8.11/8.12 are `backlog` — do not implement Language or the Settings Preferences card, but leave the seams (Task 4/5).

- **Read-before-modify current state (Step 3 analysis; files marked UPDATE):**
  - `web/src/lib/color-scheme.ts` (16 lines): a single `initColorScheme()` — `matchMedia('(prefers-color-scheme: dark)')`, `apply()` toggles `documentElement.classList` `dark`, `query.addEventListener('change', apply)`. Called once from `src/main.tsx:8` **before** `createRoot`. Comment says "No manual toggle yet". **Preserve:** the function name/signature, its main.tsx call site, and the 3 existing unit tests as-is. **Change:** `apply()` becomes preference-aware; OS listener becomes conditional on preference = system.
  - `web/index.html`: two `theme-color` metas keyed on `prefers-color-scheme` (light `#F3F8ED`, dark `#12201A`) + a synchronous inline script that only checks the OS. **Change:** single meta + stored-preference-aware script. **Preserve:** synchronous execution before CSS paint, in `<head>`, and the fallback-to-OS behavior.
  - `web/src/components/dashboard/profile-menu.tsx` (~140 lines): described in Task 5. Mounted only in NavChrome's `wide:` branch (≥660px). Exports `ProfileMenu({ email, householdId, supportsFederatedLogout })`; consumed by `nav-chrome.tsx`.
  - `web/src/components/dashboard/profile-menu.test.tsx` (existing tests listed in Task 7 — all must pass unchanged).
  - `web/src/locales/{en-US,de-DE}/translation.json`: `profileMenu` block currently has only `avatarLabel` and `profile` (en ~289, de ~289).
  - `web/src/lib/glass-classnames.ts`: `GLASS_DROPDOWN_CLASSNAME` (14px radius glass) is the menu surface — reuse, don't restyle.
  - `web/src/index.css`: `.dark` variant is `@custom-variant dark (&:is(.dark *))` — **the class on `<html>` drives everything**; the toggle only needs to set/remove that class, no per-component theme plumbing. `--ring` is `#1E7A61` light / `#8FE9CE` dark (canonical focus ring). `--nav-chrome-active-bg/-foreground` are the segment-selected tokens (`bg-nav-chrome-active-bg text-nav-chrome-active-foreground` utilities already used in `nav-chrome.tsx`/`profile-menu.tsx`). Breakpoint `wide` = 660px.

- **Cross-story context:** Story 8.1 built the Profile menu (Radix `DropdownMenu`, disabled "Profile" placeholder row, shared `useLogoff`). 8.11 adds the Language strip + a household-locale endpoint; 8.12 mounts both rows in Settings < 660px via the **same shared component** (epic: "one shared component, no duplicated logic"). Anything theme-specific must live in `appearance-row`/`color-scheme.ts`, anything reusable in `preference-strip`/`preference-row`.

- **Design-system facts (don't reinvent):** No new colors/radii/type sizes (components.md). Dark and Light are equal citizens (UX-DR11) — build and eyeball both, not just the one your OS is in. Motion: none required for the strip beyond hover/focus/press; if you add a transition, gate it under `prefers-reduced-motion: no-preference` (EXPERIENCE.md contract).

- **Guardrails from project-context.md that apply:** `verbatimModuleSyntax` (`import type`), `noUnusedLocals/Parameters`, `erasableSyntaxOnly` (no enums/param-properties — use string-literal unions), path alias `@/…`, oxlint (not ESLint), colocated tests, shadcn primitives via `npx shadcn add` only if a new primitive is genuinely needed (none expected — the strip is bespoke), no auth token in browser storage (Task 1's exception is the theme string only), i18n additive in both catalogs (AD-18).

- **Testing gotchas already known in this repo:** jsdom has no layout/media queries — assert classes/structure, prove geometry in Playwright (Stories 8.3–8.9). Radix floating primitives need `hasPointerCapture`/`scrollIntoView` stubs in jsdom (already in `profile-menu.test.tsx`). `matchMedia` is undefined in jsdom unless stubbed (`vi.stubGlobal`). `resize_window` on an existing Chrome-extension tab is unreliable — open a new correctly-sized tab.

- **Process gate reminder:** this story does not touch OIDC/auth, but it is browser-dependent behavior → live Claude-in-Chrome verification is required before `done` (Task 8). Any post-merge fix to this story needs a linked spec/story doc.

### Project Structure Notes

- New: `web/src/components/preferences/{preference-strip,preference-row,appearance-row}.tsx` (+ colocated `*.test.tsx`), `web/src/hooks/use-theme-preference.ts` (+ test if non-trivial). Modified: `lib/color-scheme.ts` (+test), `index.html`, `dashboard/profile-menu.tsx` (+test), both locale files, `web/e2e/app-shell.spec.ts`, `tests/EnergyTracker.Architecture.Tests/FrontendDoesNotStoreAuthTokensTests.cs`, `_bmad-artifacts/project-context.md`.
- Variance to record: the epic says "Extend `web/src/lib/color-scheme.ts`" — followed. The only structural deviation is the guard allowlist (Task 1), forced by an existing invariant test; the epic doesn't mention it.

### References

- [Source: _bmad-artifacts/planning/epics/epic-8-desktop-tablet-layout-correction.md#Story 8.10]
- [Source: _bmad-artifacts/planning/prds/prd-energy-tracker-2026-08-08/prd/4-features.md#FR-35]
- [Source: _bmad-artifacts/planning/epics/requirements-inventory.md — UX-DR32, UX-DR33, FR-35]
- [Source: _bmad-artifacts/planning/ux-designs/ux-energy-tracker-2026-08-08/DESIGN/components.md — Profile menu, Preference icon strip]
- [Source: _bmad-artifacts/planning/ux-designs/ux-energy-tracker-2026-08-08/EXPERIENCE.md — Profile menu]
- [Source: _bmad-artifacts/planning/ux-designs/ux-energy-tracker-2026-08-08/mockups/key-profile-preferences.html]
- [Source: _bmad-artifacts/implementation/8-1-responsive-nav-chrome-profile-menu.md — Profile menu origin, live-verification workaround]
- [Source: _bmad-artifacts/implementation/8-9-tariff-comparison-side-by-side-summary.md — e2e/live-check idioms]
- [Source: _bmad-artifacts/project-context.md — Security, Testing, Process gates]
- [Source: tests/EnergyTracker.Architecture.Tests/FrontendDoesNotStoreAuthTokensTests.cs]
- [Source: web/src/lib/color-scheme.ts, web/index.html, web/src/components/dashboard/profile-menu.tsx]

## Dev Agent Record

### Agent Model Used

claude-sonnet-5-5

### Debug Log References

- Playwright: locators for `Dark`/`Light` needed `exact: true` (substring also matched "System — currently Dark"). Certs moved aside/restored; port 4173 clear afterwards.

### Completion Notes List

- Tasks 1–3, 5–7 implemented and green: web vitest 480 pass, `tsc -b` clean, oxlint no new warnings (pre-existing only), `dotnet test` Architecture 6/6 (guard proven narrow with a temporary probe file, removed), Playwright 7/7.
- Guard allowlist: `lib/color-scheme.ts` + `.test.ts`, plus an existence assertion. Because of that, `appearance-row.test.tsx` asserts via `getThemePreference()` instead of reading storage; storage writes are covered in `color-scheme.test.ts`.
- Radix: arrow/Home/End handled in strip with stopPropagation; menu stays open on segment click; Escape returns focus to trigger — all verified in jsdom.
- i18n: new top-level `preferences.appearance.*` block in both catalogs; key sets diffed manually (identical); no parity test exists.
- **Live Chrome verification (Task 8), real household, de-DE, ~1511px, Light + Dark:** row renders per mockup (Darstellung / Dieses Gerät, 296px menu); Dark applies immediately with menu open, `theme-color` meta follows (#12201A / #F3F8ED), single meta; stored `dark` survives reload (no flash, class present on load); all four top-nav screens stay dark; Escape closes the menu and focus returns to "Konto-Menü"; ←/→/Home/End move+select.
- **Live findings fixed:** (1) Keyboard users could not reach the strip — Radix swallows Tab and the strip isn't a menu item (menu opens focused on "Abmelden"). Added `cycleTabStops` on the menu content: Tab/Shift+Tab cycle strip checked segment ↔ first enabled item; unit test added. (2) Hit area measured 42px (pseudo inset is inside the 1px border); changed to `-inset-y-[3px]` → 44px, verified live.
- Ralf verified the real macOS appearance flip (System follows OS live) manually — works. Log off/back in persistence not exercised live (logoff code never touches the stored key; storage survives by design). English copy covered by unit tests (live check was de-DE).
- Device theme reset to System after testing.
- AC accounting: **#1** profile-menu/appearance-row tests + live render; **#2** strip/profile-menu unit tests (incl. Tab cycle, Escape focus return) + live keyboard check; **#3** appearance-row test (no fetch) + e2e + live; **#4** color-scheme tests + Ralf's live OS flip; **#5** e2e reload/no-flash + live reload + meta; **#6** color-scheme tests (throwing getItem/unknown value); **#7** appearance-row test with `navigator.onLine=false`, no fetch; **#8** both catalogs (key sets identical) + live Light/Dark.

### File List

- tests/EnergyTracker.Architecture.Tests/FrontendDoesNotStoreAuthTokensTests.cs
- _bmad-artifacts/project-context.md
- web/index.html
- web/src/lib/color-scheme.ts
- web/src/lib/color-scheme.test.ts
- web/src/hooks/use-theme-preference.ts
- web/src/components/preferences/preference-strip.tsx
- web/src/components/preferences/preference-strip.test.tsx
- web/src/components/preferences/preference-row.tsx
- web/src/components/preferences/appearance-row.tsx
- web/src/components/preferences/appearance-row.test.tsx
- web/src/components/dashboard/profile-menu.tsx
- web/src/components/dashboard/profile-menu.test.tsx
- web/src/locales/en-US/translation.json
- web/src/locales/de-DE/translation.json
- web/e2e/app-shell.spec.ts
- _bmad-artifacts/implementation/sprint-status.yaml

### Change Log

- 2026-09-30: Story 8.10 created — ready-for-dev.
- 2026-09-30: Implemented theme preference (lib, inline script, strip/row components, Profile menu, i18n, guard allowlist, tests). Live Chrome verification done; fixed Tab reachability of the strip and 44px hit area. Status → review.

### Review Findings

- [x] [Review][Decision] (resolved: live checks run in the review session, see below) Task 8 ticked `[x]` with only partial live verification — Completion Notes admit log-off/back-in persistence (AC #5) was not exercised live, English copy was only unit-tested (live check was de-DE), and "Light chosen + OS Dark → light theme-color" was only covered by e2e. Task 8 says not to tick on unit/e2e proof alone, and the live gate blocks review→done. Options: run the missing live Chrome checks, or untick Task 8 and record accepted deviations.
- [x] [Review][Decision] (resolved 2026-09-30: accepted and documented — AC #2 reworded; matches Radix's arrows-for-items / Tab-for-widgets convention and scales to two strips in 8.11) AC #2 deviation: Tab cycles between the strip's checked segment and the first enabled menu item (`cycleTabStops`) instead of "Tab enters the group on the checked segment" in DOM order; other menu items are reachable only via arrows. Rationale is in Completion Notes but not flagged as a deviation. Accept + document (and revisit for two strips in 8.11), or change the behaviour.
- [x] [Review][Patch] No-flash e2e assertion is weak [web/e2e/app-shell.spec.ts] — `reload({waitUntil:'commit'})` + `readyState !== 'loading'` can pass after `initColorScheme()` ran, so it does not prove the inline script applied the class. Also never covers a fresh load with Light stored + OS dark. Use `addInitScript` + DOMContentLoaded capture (or block `/src/main.tsx`), and reload with dark emulation before asserting.
- [x] [Review][Patch] Typeahead keys inside the strip bubble to Radix and steal focus to a menu item [web/src/components/preferences/preference-strip.tsx:51]
- [x] [Review][Patch] Strip key handler hijacks modified keys (Alt+Arrow = browser back, Ctrl/Cmd+Home/End) — return early on alt/ctrl/meta [web/src/components/preferences/preference-strip.tsx:51]
- [x] [Review][Patch] `initColorScheme` adds the matchMedia `change` listener on every call and throws on Safari <14 (no `addEventListener` on MediaQueryList) before `createRoot` — attach once, fall back to `addListener` [web/src/lib/color-scheme.ts:90]
- [x] [Review][Patch] Theme-guard allowlist matches by `"/" + suffix`, so any `*/lib/color-scheme.ts` under `web/src` is exempt — compare the path relative to `web/src` exactly [tests/EnergyTracker.Architecture.Tests/FrontendDoesNotStoreAuthTokensTests.cs:57]
- [x] [Review][Patch] Unused i18n key `preferences.appearance.system` (row uses `systemCurrently`); `resolvedLight`/`resolvedDark` duplicate `light`/`dark` [web/src/locales/*/translation.json]
- [x] [Review][Patch] Stale Completion Note ("Tab behavior … still need live Chrome verification") contradicts later notes that say it was verified and fixed [8-10-theme-toggle-profile-menu.md]
- [x] [Review][Patch] Found live during review: System segment read "System — currently Dark" on a light OS when Dark was pinned (it reported the applied theme, not the OS scheme). Now uses `getSystemTheme()`, and OS changes notify subscribers even while Light/Dark is pinned; test added [web/src/lib/color-scheme.ts, web/src/hooks/use-theme-preference.ts]
- [x] [Review][Defer] `PreferenceStrip` ignores `pending` for input (second click/arrow fires another `onChange` mid-save), has no "saving" announcement, and derives the arrow index from `value` rather than the focused segment — deferred, matters only once 8.11 makes the strip async
- [x] [Review][Defer] Radix `role="menu"` containing non-menuitem radiogroups (a11y semantics; Popover fallback never evaluated) — deferred, design-level decision to revisit with 8.11/8.12
- [x] [Review][Defer] `whitespace-nowrap` label/sub-label in `PreferenceRow` will overflow in the narrow Settings card — deferred to 8.12
- [x] [Review][Defer] `animate-spin` spinner ignores `prefers-reduced-motion`; no drift test between `index.html` inline script and `color-scheme.ts` — deferred, low

#### Review session: patches applied + live verification (2026-09-30)

- Patches: e2e no-flash proof replaced (inline script alone, app bundle blocked; stored dark/OS light, stored light/OS dark, nothing/OS dark) plus reload persistence assertion; typeahead keys and alt/ctrl/meta keys no longer captured by the strip; `initColorScheme` no longer stacks OS listeners and falls back to `addListener` (Safari < 14); guard allowlist compares the exact path relative to `web/src`; unused/duplicate i18n keys removed; stale Completion Note removed. New unit tests cover each. Web vitest 486 pass, `tsc -b` clean, Architecture 6/6, Playwright 10/10.
- Live Chrome (local stack via `scripts/run-api.sh` + Vite on https://localhost:5173, existing signed-in test-user session): de-DE — Dunkel applies with the menu open, `theme-color` #12201A, storage `dark`, typed "a" keeps focus on the strip (no jump to "Abmelden"), Alt+← ignored, Tab/Shift+Tab cycle strip ↔ first item, hit area 44px. en-US (switched via `i18nextLng`) — copy "Appearance / This device / System — currently Light / Light / Dark / Profile / Log off"; Dark persists across reload; Light applies, meta #F3F8ED, persists across reload; **log off → theme key still `light` afterwards (AC #5)**. Browser state restored (de-DE, System). Signing back in was not performed (Auth0 password entry is left to the user).
- Still e2e-only: "Light chosen while the OS is Dark" (the OS scheme cannot be flipped from the browser session).
- Decision 2 resolved by Ralf: accept the Tab cycle and document it (AC #2 reworded). Light-chosen-with-OS-dark was verified live by Ralf earlier.
- 2026-09-30: Code review complete — patches applied, live Chrome verification done, System-label bug fixed, both decisions resolved. Status → done.
