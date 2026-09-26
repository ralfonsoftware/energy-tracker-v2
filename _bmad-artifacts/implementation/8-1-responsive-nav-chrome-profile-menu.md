---
baseline_commit: e8ac01f63acd37704781c41500e60a87fe03d744
---

# Story 8.1: Responsive Nav Chrome & Profile Menu

Status: done

<!-- Note: Validation is optional. Run validate-create-story for quality check before dev-story. -->

## Story

As a Household member on a tablet or desktop browser,
I want the navigation to appear at the top instead of the bottom, and a central Profile menu to reach Log off,
so that I don't have to reach the bottom edge of a wide screen or scroll through Settings to sign off.

## Acceptance Criteria

1. **Given** a Household member opens the app at an available width ≥660px, **when** the page loads, **then** the navigation (Dashboard/Trend History/Tariff Radar/Settings) renders as a horizontal bar at the top instead of the bottom tab bar, using the identical active-state tokens the bottom tab bar already uses — `bg-nav-chrome-active-bg`/`text-nav-chrome-active-foreground` and their `.dark` pairs (UX-DR9).
2. **Given** the same view at an available width <660px, **when** the page loads, **then** the existing bottom tab bar renders unchanged — no regression for mobile.
3. **Given** the top nav is visible (≥660px), **when** the member clicks the avatar button on the right, **then** a dropdown opens showing the account email, a "Profile" entry, and a "Log off" entry.
4. **Given** the profile dropdown is open, **when** the member clicks "Log off", **then** the exact same logoff flow from Story 1.12 starts (offline-queue check, federated-logout warning where applicable, navigation to `/logout`) — no new logoff logic, only a second entry point (UX-DR27, FR-33).
5. **And** the existing Logoff control on the Settings page (<660px) remains unchanged.

## Tasks / Subtasks

- [x] **Task 1: Establish the shared 660px breakpoint infrastructure** (AC #1, #2)
  - [x] Add `--breakpoint-wide: 660px;` inside `web/src/index.css`'s `@theme inline { ... }` block (lines 7-93). Tailwind v4's `--breakpoint-*` theme namespace auto-generates a matching responsive variant (here: `wide:`, equivalent to `@media (min-width: 660px)`) — the same mechanism this file already uses for its custom `--color-*`/`--radius-*`/`--spacing-*` tokens (e.g. `--radius-glass-sm`, `--spacing-card-padding`).
  - [x] This repository has **never registered a custom breakpoint before** — no `--breakpoint-*` entry exists in `index.css` today, and the literal value `660` appears nowhere in `web/src` code (only in UX design docs). Verify the generated `wide:` variant actually compiles and behaves as expected (`npm --prefix web run build`, or inspect in a browser) before relying on it elsewhere — there's no existing precedent in this codebase to copy.
  - [x] This is deliberately the **shared infrastructure** the rest of Epic 8 depends on — Stories 8.2–8.5 reuse this same `wide:` variant for their own 660px-column layout constraints. Get the token name right here.

- [x] **Task 2: Add the top-nav variant to `NavChrome` and reposition it visually** (AC #1, #2)
  - [x] `web/src/components/dashboard/nav-chrome.tsx` (67 lines today) renders one bottom-tab-bar `<nav>` from `{ active, onDashboardClick, onTrendHistoryClick, onTariffRadarClick, onSettingsClick }`. Extend it to render a **second, top-nav `<nav>` variant from the exact same props** — don't create a second component or duplicate the active/inactive logic.
  - [x] Wrap the existing bottom-tab-bar markup with `wide:hidden`; add the new top-nav markup wrapped `hidden wide:flex`. Top-nav content, left to right: brand wordmark (`t('app.title')` + the same mark glyph Dashboard's topbar uses), the 4 nav links (reuse `ACTIVE_CLASSNAME` — `bg-nav-chrome-active-bg text-nav-chrome-active-foreground` — verbatim, never a new color), then the `ProfileMenu` (Task 3) on the far right.
  - [x] **Do not restructure the 4 page files' JSX.** `NavChrome` is currently mounted once at the *bottom* of each page's `flex flex-col` `<main>` (`dashboard-page.tsx:176`, `trend-history-page.tsx:94`, `tariff-radar-page.tsx:79`, `settings-page.tsx:181`). Reposition the wide-variant to the visual top via a flex `order` utility on NavChrome's own wrapper (e.g. `wide:order-first`) rather than moving the call site — the single existing mount point already sits as a direct flex child of each page's column, so `order` alone repositions it. Confirm this actually renders at the top in a live browser (Task 8), not just visually plausible from the class names.
  - [x] **Scope boundary — do not merge in each page's own topbar.** Every page still has its own `<h1>` title (and Dashboard has its Import/Event icon buttons) rendered separately above where `NavChrome` sits; at ≥660px this means the new brand top-nav bar and each page's own title row both render (some visual duplication is expected and acceptable here). Reconciling that (icon-label buttons, page-title placement) is explicitly Story 8.2+'s job (UX-DR23–26, per-screen), not this one — don't scope-creep into it.

- [x] **Task 3: Build the Profile menu component** (AC #3)
  - [x] No `DropdownMenu` UI primitive exists yet under `web/src/components/ui/` (contents today: `badge, button, card, dialog, glass-card, input, label, select, sheet, skeleton, switch, table, unit-input`). Add `web/src/components/ui/dropdown-menu.tsx` via `npx shadcn add dropdown-menu` — the `radix-ui` package (`web/package.json`) is already a dependency, no new install needed — or hand-author it mirroring `web/src/components/ui/select.tsx`'s exact house style (`"use client"`, `data-slot` attributes, `cn()` from `@/lib/utils`, `data-[state]` animation utility classes, a `<Portal>` wrapper for floating content). `select.tsx` is the closest existing Radix-floating-primitive analog in this repo — match its shape, don't invent a new pattern.
  - [x] Add `web/src/components/dashboard/profile-menu.tsx` — same folder as `nav-chrome.tsx`, matching its established "lives in `dashboard/`, consumed by every page" precedent. Do not create a new `nav/` or `layout/` folder; none exists and this codebase groups by feature, not by UI-pattern.
  - [x] Avatar trigger button: reuse the existing `bg-nav-chrome-active-bg`/`text-nav-chrome-active-foreground` Tailwind classes (already used this way for icon buttons at `dashboard-page.tsx:134,148`) for its fill — do not invent a new avatar gradient/token. The mockup's `.profile-avatar` radial gradient is a proposal-stage visual reference only, not a confirmed design token.
  - [x] Dropdown panel styling: match this app's glass-panel language, but at the **smaller** radius the mockup itself specifies for a menu (`.profile-dropdown { border-radius: 14px }` = `--radius-glass-sm`), not the `--radius-glass-lg` (28px) that `GLASS_MODAL_CLASSNAME` (`web/src/lib/glass-classnames.ts`) uses for full-screen modals. Reuse `GLASS_MODAL_CLASSNAME`'s border/background/blur *values*, swapping only its radius class, rather than copy-pasting the mockup's literal hardcoded `rgba(24,38,31,0.98)` etc.
  - [x] **"Profile" row has no defined destination.** Checked the PRD, epics, and UX docs in full — nothing specifies what clicking "Profile" should do; only the dropdown's existence and contents are specified. Render it as a visibly present but non-interactive/disabled row for this story — do not invent a new Profile page/route, and do not give it a silent no-op `onClick` (reads as a bug, not a placeholder). Note this as an open question in Completion Notes for a future story to resolve.
  - [x] Only mount `ProfileMenu` inside the `wide:flex` top-nav branch — per `DESIGN/components.md`'s "Profile menu" section, the bottom-tab-bar variant (<660px) has no equivalent; Story 1.12's Settings-page control remains the only <660px path.

- [x] **Task 4: Wire "Log off" to the existing flow — no new logic, one shared state machine** (AC #4, #5)
  - [x] Today the entire confirm → queue-warning/federated-warning dialog state machine (`logoffStep`, `logoffChecking`, `pendingReadingCount`, and the `handleConfirmLogoff`/`proceedPastQueueCheck`/`navigateToLogout` handlers) lives inline inside `web/src/components/settings/settings-page.tsx`. Only `web/src/lib/logoff.ts`'s `checkPendingReadingsBeforeLogoff(householdId)` is already a standalone, reusable piece.
  - [x] Extract the state machine into a shared hook — recommended: `web/src/hooks/use-logoff.ts` (this repo has no `hooks/` folder in active use yet, but `web/components.json` already reserves the `"hooks": "@/hooks"` alias for exactly this; `web/src/lib/` is a defensible alternative if you have a reason to prefer it, but pick one and be consistent). It takes `householdId`/`supportsFederatedLogout`, returns the step/checking/count state plus the three handlers. Both `SettingsPage`'s existing control and the new `ProfileMenu`'s "Log off" row consume the same hook instance-shape (each still needs its own mounted `<Dialog>`, since they render in different places/pages) — this is what the epic means by "no new logoff logic, only a second entry point."
  - [x] **AC #5 is about behavior and appearance, not code structure** — refactoring `SettingsPage`'s internals to consume the new hook is expected and fine; its trigger label, dialog copy, and step sequence must not change. Re-run `settings-page.test.tsx`'s existing "Logoff control" test suite with unchanged assertions to prove this (only the internal wiring should differ).
  - [x] `ProfileMenu` needs `householdId` and `supportsFederatedLogout` — thread them from `App.tsx` (`supportsFederatedLogout` state at `App.tsx:41`; `state.household.id`) through each page's existing prop chain, the same way `onSettingsClick` etc. already flow into `dashboard-page.tsx`, `trend-history-page.tsx`, `tariff-radar-page.tsx`, `settings-page.tsx`. All four page prop interfaces need `householdId`/`supportsFederatedLogout` added (Settings already receives both).
  - [x] Reuse `GLASS_MODAL_CLASSNAME` and the exact same 3-step dialog copy (`t('settings.logoff.*')` keys) for the Profile menu's own `<Dialog>` — don't duplicate the strings under a new namespace (Task 6).

- [x] **Task 5: Backend — expose the account email on `/api/session`** (AC #3)
  - [x] `SessionEndpoints.cs`'s `SessionResponse` record (`src/EnergyTracker.Api/Endpoints/SessionEndpoints.cs:69`) has no `Email` field. Add one — e.g. `public record SessionResponse(bool HasHousehold, Guid? HouseholdId, string? Locale, string? Currency, bool SupportsFederatedLogout, string? Email);`. Pure DTO change, no EF Core entity, no migration.
  - [x] **Do not read `ClaimTypes.Email` directly and assume it's populated — this exact bug class already happened once.** Story 3.6 found `ClaimTypes.Name` never populates against this app's Auth0 config in production: `Program.cs` sets `GetClaimsFromUserInfoEndpoint = true` with no explicit `ClaimActions` mapping, so userinfo-sourced claims keep their **raw JSON key** (`"name"`) instead of being remapped to `ClaimTypes.Name` the way ID-token claims are — a gap only a live Auth0 session revealed. The identical mechanism applies to `"email"` vs `ClaimTypes.Email`.
  - [x] Mirror the existing fix: `src/EnergyTracker.Application/HouseholdClaimTypes.cs`'s `ResolveDisplayName(ClaimsPrincipal user)` checks the raw `"name"` claim first, falls back to `ClaimTypes.Name`, treats an empty string as absent. **Read that file in full before writing this** (it's 34 lines) and add a parallel `ResolveEmail(ClaimsPrincipal user)` to the same static class, same raw-claim-first / `ClaimTypes.Email`-fallback / empty-string-as-absent shape.
  - [x] Add a `ClaimsPrincipal user` parameter to `SessionEndpoints.cs`'s minimal-API handler (ASP.NET Core binds this automatically — no new DI registration needed) and call `HouseholdClaimTypes.ResolveEmail(user)`, mirroring `HouseholdEndpoints.cs:60`'s existing `HouseholdClaimTypes.ResolveDisplayName(user)` call.

- [x] **Task 6: i18n** (AD-18)
  - [x] Add new keys to **both** `web/src/locales/en-US/translation.json` and `web/src/locales/de-DE/translation.json` — never ship one locale without the matching key in the other. Suggested: a `profileMenu` object (e.g. `profileMenu.profile`) alongside the existing `dashboard.nav.*` block.
  - [x] **Reuse, don't duplicate:** the dropdown's "Log off" row label and its entire 3-step dialog copy already exist as `settings.logoff.trigger`, `settings.logoff.confirm*`, `settings.logoff.queueWarning*`, `settings.logoff.federatedWarning*` (`web/src/locales/en-US/translation.json:43-57`). Reuse these `t()` keys verbatim from `ProfileMenu` — do not create a second copy of this copy under a new namespace. This is exactly what AD-18 ("resource-file addition, never a code branch") and the epic's "no new logoff logic" language are pointing at.

- [x] **Task 7: Automated test coverage**
  - [x] Backend: extend `tests/EnergyTracker.Application.Tests/HouseholdClaimTypesTests.cs` with the same 5-case pattern already used for `ResolveDisplayName` (raw-preferred / falls-back-when-absent / falls-back-when-raw-is-empty / null-when-neither-present / null-when-both-empty), applied to `ResolveEmail`. Extend `tests/EnergyTracker.Api.Tests/SessionEndpointsTests.cs` (and/or `SessionAndHouseholdCreationTests.cs`, which already has a live-integration-style `/api/session` assertion) to cover the new `Email` field being present/absent as expected.
  - [x] Frontend component tests (Vitest + Testing Library, colocated, matching `nav-chrome.test.tsx`'s existing conventions — checks `aria-current="page"` and the `bg-nav-chrome-active-bg`/`text-nav-chrome-active-foreground` classes): extend `nav-chrome.test.tsx` to assert both variants are present in the DOM with the right classes/active state. Add `profile-menu.test.tsx`: dropdown opens on click, shows email/Profile/Log off rows, clicking Log off drives the shared hook the same way `settings-page.test.tsx` already proves for the Settings entry point (assert via the hook's observable behavior, don't re-run 1.12's entire test suite a second time).
  - [x] **jsdom does not evaluate real CSS media queries.** A Vitest/Testing-Library test can only assert that both markup variants exist with the correct `wide:hidden`/`hidden wide:flex` classes — it cannot prove which one is actually visible at a given viewport width. That proof needs a real browser: extend `web/e2e/app-shell.spec.ts` (or add a new Playwright spec) using `page.setViewportSize()` — e.g. assert the bottom tab bar is visible and the top nav is not at 375×800, and the reverse at 900×800. This is the actual regression guard for AC #1/#2, the component test alone is not sufficient.
  - [x] Run the full backend (`dotnet test`) and frontend (`npm --prefix web run test`, `tsc -b`, `oxlint`) suites clean, plus the new/extended Playwright spec, before marking any task complete.

- [x] **Task 8: Live Auth0 + Chrome verification (mandatory — do not defer to a later story)**
  - [x] This story triggers this project's standing live-verification gate on **two independent counts**: it touches OIDC claims (Task 5's new `Email` resolution follows the exact raw-JSON-claim-vs-`ClaimTypes` ambiguity that produced a real, live-only-detectable bug in Story 3.6), and it's browser-dependent responsive/interaction behavior (breakpoint swap, dropdown open/close). A story like this cannot move review→done without a live Auth0/Chrome verification actually performed in this session — if Chrome isn't connected, raise it immediately and pause rather than deferring or letting review catch the gap later.
  - [x] Verify live, specifically: (a) `/api/session`'s `Email` field returns the real Auth0 test-user's actual email — not null/empty — proving the raw-claim-first resolution really works against this app's live Auth0 config (exactly the check Story 3.6 skipped for `name` until code review caught it); (b) resizing the real browser window across 660px actually swaps bottom-tab-bar ↔ top-nav; (c) opening the Profile dropdown and clicking "Log off" runs the complete 1.12 flow end-to-end (queue check, federated-logout warning where applicable, lands back at Auth0/login) — not just that a click handler fires.

- [x] **Task 9: Verify against every AC**
  - [x] Walk AC #1-#5 individually and state, in Completion Notes, exactly what proves each one — code reference, automated test, or live verification (Task 8) — following the same AC-by-AC accounting Story 1.12/1.7 used.

### Review Findings

- [x] [Review][Patch] Two independent, simultaneously-reachable "Log off" entry points on the Settings page at ≥660px — `settings-page.tsx`'s inline Logoff `<Button>` has no `wide:hidden` treatment, so at wide widths it sits alongside `NavChrome`'s new Profile-menu "Log off" row, each backed by its own separate `useLogoff` hook instance and its own mounted `<Dialog>`. **Decision (Ralf, 2026-09-26): hide the inline button at wide widths** — Profile menu becomes the sole ≥660px entry point, matching how the bottom-tab-bar itself is hidden at wide widths elsewhere in this diff. [web/src/components/settings/settings-page.tsx] Fixed: added `wide:hidden` to the inline Logoff `<Button>`.
- [x] [Review][Blocked] Task 8's live-verification gate: the AC #2 breakpoint-swap-direction check ("resizing the real browser window across 660px actually swaps bottom-tab-bar ↔ top-nav") was not actually performed live. The Debug Log self-discloses that `resize_window` didn't change the sandbox tab's real `window.innerWidth`, and substitutes a real-Chromium Playwright spec (`web/e2e/app-shell.spec.ts`) instead. **Decision (Ralf, 2026-09-26): the Playwright substitute is not accepted — a real live Claude-in-Chrome resize check across 660px is still required before this story can move to `done`.** Resolved (2026-09-26, code review): reproduced the identical `resize_window`-on-an-already-rendered-tab limitation, then worked around it — resizing a **new** tab before navigation does take effect. Live-verified both sides against the real dev stack (Postgres + API + Vite, real Auth0 test session, `test@dummy.com`): a tab created at 1000px `window.innerWidth` renders the top nav (wordmark, 4 links, Profile menu with real email) with no bottom tab bar; a tab created at 500px renders the bottom tab bar with no top nav/Profile menu and exactly one reachable "Log off" control (the inline Settings button, per the AC #5/duplicate-entry-point fix above). Genuine live-Chrome evidence now covers all of AC #1-#3/#5; this gate is satisfied.
- [x] [Review][Patch] `ProfileMenu`'s `DropdownMenuItem onSelect={openLogoffDialog}` opens the Logoff `<Dialog>` synchronously while Radix's `DropdownMenu` is still closing — the known Radix dismissable-layer race between a closing menu and an opening dialog, which can leave the page's pointer-events stuck or the dialog unfocused. [web/src/components/dashboard/profile-menu.tsx:70-74] Fixed: `onSelect={(e) => { e.preventDefault(); requestAnimationFrame(openLogoffDialog) }}`, matching the pattern shadcn's own docs recommend for a Dialog triggered from a DropdownMenuItem.
- [x] [Review][Patch] No integration/page-level test exercises the new top-nav variant through a real page's prop chain — `dashboard-page.test.tsx`, `trend-history-page.test.tsx`, and `App.test.tsx` all resolve the new duplicate nav buttons via `getAllByRole(...)[0]`, which by DOM order is always the bottom-tab-bar instance; the top-nav path is proven only by the isolated `nav-chrome.test.tsx` unit test with mocked callbacks, never through an actual page component. `tariff-radar-page.tsx` also has no test file at all covering its new `NavChrome` wiring (verified `App.tsx` does pass the 3 new required props correctly, so this is a coverage gap, not a build break). [web/src/components/dashboard/dashboard-page.test.tsx, trend-history-page.test.tsx, App.test.tsx] Fixed: added a new `dashboard-page.test.tsx` test exercising the `[1]`/top-nav Settings button and the Profile menu's email row through the real page tree.
- [x] [Review][Patch] The literal line that fixes the live-discovered OIDC bug (`options.Scope.Add("email")` in `Program.cs`) has no automated regression coverage — `TestAuthHandler` bypasses real OIDC scope negotiation entirely (it adds the `"email"` claim from a test header regardless of what `options.Scope` contains), so a future accidental removal of that line would only be caught by another live Auth0 session. [src/EnergyTracker.Api/Program.cs] Fixed: added `tests/EnergyTracker.Api.Tests/OidcConfigurationTests.cs`, asserting the configured `OpenIdConnectOptions.Scope` includes `"email"` via a dedicated `WebApplicationFactory<Program>` with dummy Oidc settings, no real IdP call.
- [x] [Review][Patch] `web/src/components/ui/dropdown-menu.tsx` is missing the `"use client"` directive that Task 3 explicitly instructed mirroring from `select.tsx`'s "exact house style" — `select.tsx` and `dialog.tsx` (this codebase's other floating Radix primitives) both have it, this one doesn't. Functionally inert in this Vite SPA today, but a real, one-line miss against an explicit instruction that future `shadcn add` runs will keep reintroducing. [web/src/components/ui/dropdown-menu.tsx:1] Fixed.
- [x] [Review][Patch] Completion Notes for AC #2 overstate the bottom tab bar as "otherwise byte-for-byte unchanged" — the `<nav>` itself gained `data-slot="nav-chrome-bottom"` and `wide:hidden`, and lost `mt-auto` (moved to a new wrapping `<div>`). Functionally equivalent at <660px, but the claim is inaccurate. [web/src/components/dashboard/nav-chrome.tsx:47; _bmad-artifacts/implementation/8-1-responsive-nav-chrome-profile-menu.md Completion Notes] Fixed: corrected the AC #2 Completion Note wording to describe the actual restructuring.

Dismissed as noise/handled elsewhere (4): the story's own "mark glyph Dashboard's topbar uses" reference doesn't correspond to anything in the actual Dashboard topbar (plain text there too, no glyph) — the implementation correctly matches real Dashboard markup; `ProfileMenu` being unconditionally mounted at every viewport width is consistent with this codebase's pre-existing "always mounted, CSS-toggled visibility" pattern already used for the bottom tab bar, not a new deviation; the disabled "Profile" row shipping with no defined destination is explicitly authorized by this story's own Task 3 instructions and already flagged as an open question in Completion Notes; the test fixture's `ralf@example.com` placeholder is cosmetic and doesn't violate an established convention (most tests in this file use bare GUID subjects with no email/name at all).

## Dev Notes

- **This is a UI-only story with no new domain capability** — Epic 8's own framing. It reuses Story 1.12/FR-33's logoff mechanism verbatim (AD-17) and adds no new architecture decision; only AD-17 (logoff reuse) and AD-18 (i18n) apply.
- **No React Router exists in this codebase.** `web/src/App.tsx` holds a single `view` state (`useState<'dashboard'|'settings'|'trendHistory'|'tariffRadar'|'smartPlugImport'>`) and branches `if (view === 'x') return <XPage/>` — navigation is prop-drilled `onXClick` callbacks, not URL-derived. `NavChrome`'s `active` prop is a literal string matching whichever page is rendering it, not derived from a route. The top-nav variant needs no router-awareness — it's the same `active`/`onXClick` props NavChrome already receives, just a second rendering of them.
- **This is the first story to introduce the 660px breakpoint into actual frontend code.** It exists today only as a design-spec value (`{spacing.breakpoint-wide}` in `_bmad-artifacts/planning/ux-designs/ux-energy-tracker-2026-08-08/DESIGN/layout-spacing.md`) — no `useMediaQuery`/`useBreakpoint` hook, no Tailwind breakpoint token, no CSS media query at 660px exists anywhere in `web/src` yet. Task 1's `--breakpoint-wide` token is genuinely new infrastructure, not a reuse of an existing pattern — treat it carefully since Stories 8.2-8.5 build on it.
- **Design tokens to reuse verbatim (do not invent new ones):**
  - Nav active state: `--color-nav-chrome-active-bg`/`--color-nav-chrome-active-foreground` (theme aliases in `web/src/index.css`), concretely `--nav-chrome-active-bg: rgba(30,122,97,0.14)` / `--nav-chrome-active-foreground: #1E7A61` (light), `rgba(47,179,151,0.16)` / `#6FD1B1` (dark). Already exposed as Tailwind classes `bg-nav-chrome-active-bg`/`text-nav-chrome-active-foreground`, already used by `nav-chrome.tsx`'s `ACTIVE_CLASSNAME` and by icon buttons in `dashboard-page.tsx`.
  - Glass modal styling: `GLASS_MODAL_CLASSNAME` in `web/src/lib/glass-classnames.ts` — reuse its color/blur values for the dropdown panel, but swap its `rounded-glass-lg` (28px) for `rounded-glass-sm` (14px), matching the mockup's own `.profile-dropdown` radius for a compact menu vs. a full modal.
  - `{colors.surface-quiet}` referenced elsewhere in Epic 8's UX docs **does not exist as a CSS custom property yet** (only its private, ungeneralized precursor `--tariff-check-card-bg`/`-border` exists) — not needed for this story (no quiet-tier card here), but don't assume it's already wired up if cross-referencing later stories' specs.
- **Profile avatar/email content:** no account email is available anywhere in the frontend today (`SessionResponse` in `App.tsx` has no `email` field) — Task 5 is a real backend change, not just a frontend read of already-available data. No `HouseholdMember`/domain entity stores email either (verified: no `Email` field anywhere under `src/EnergyTracker.Domain/`) — it must come from the live OIDC claims at session-request time, matching this app's existing "read identity from claims each session, don't persist a duplicate copy" pattern (`ResolveDisplayName` does the same for the display name at Household-creation time, not persisted either).
- **Testing standard reminder:** this repo's frontend unit tests are colocated next to source (`nav-chrome.test.tsx` beside `nav-chrome.tsx`), not a parallel `__tests__/` tree; backend tests use Shouldly assertions and NSubstitute mocking, never raw xUnit `Assert.*` or hand-rolled fakes (see `project-context.md`).

### Project Structure Notes

```text
energy-tracker-v2/
  src/EnergyTracker.Application/
    HouseholdClaimTypes.cs                  # modified — new ResolveEmail(ClaimsPrincipal), mirrors
                                             # ResolveDisplayName's raw-claim-first pattern (Task 5)
  src/EnergyTracker.Api/
    Endpoints/
      SessionEndpoints.cs                   # modified — SessionResponse gains Email; handler gains
                                             # a ClaimsPrincipal parameter (Task 5)
  tests/EnergyTracker.Application.Tests/
    HouseholdClaimTypesTests.cs             # extended — ResolveEmail test cases (Task 7)
  tests/EnergyTracker.Api.Tests/
    SessionEndpointsTests.cs                # extended — Email field coverage (Task 7)

  web/src/
    index.css                               # modified — new --breakpoint-wide: 660px in @theme
                                             # inline block (Task 1)
    App.tsx                                 # modified — SessionResponse gains email; householdId/
                                             # supportsFederatedLogout threaded to all 4 pages, not
                                             # just SettingsPage (Task 4)
    hooks/
      use-logoff.ts                         # new — extracted shared logoff state machine (Task 4);
                                             # new folder, matches components.json's reserved
                                             # "hooks": "@/hooks" alias (not yet used anywhere)
      use-logoff.test.ts                    # new
    components/
      ui/
        dropdown-menu.tsx                   # new — shadcn/radix-ui primitive, mirrors select.tsx
                                             # (Task 3); no new npm dependency (radix-ui already
                                             # installed)
      dashboard/
        nav-chrome.tsx                      # modified — adds the wide:flex top-nav variant beside
                                             # the existing wide:hidden bottom-tab-bar (Task 2)
        nav-chrome.test.tsx                 # extended
        profile-menu.tsx                    # new — avatar + dropdown (Task 3), mounted only inside
                                             # NavChrome's top-nav variant
        profile-menu.test.tsx               # new
      dashboard/dashboard-page.tsx           # modified — householdId/supportsFederatedLogout props
      trend-history/trend-history-page.tsx   # modified — same
      tariff/tariff-radar-page.tsx           # modified — same
      settings/settings-page.tsx             # modified — refactored to consume the shared
                                             # use-logoff hook; trigger/dialog UI unchanged (AC #5)
      settings/settings-page.test.tsx        # unchanged assertions expected (internal wiring only)
    locales/
      en-US/translation.json                # modified — new profileMenu.* keys (Task 6)
      de-DE/translation.json                # modified — matching keys

  web/e2e/
    app-shell.spec.ts                       # extended (or new sibling spec) — viewport-resize
                                             # breakpoint assertion (Task 7)
```

No changes expected to `web/src/lib/offline-queue.ts`, `web/src/lib/meter-reading-sync.ts`, or `web/src/lib/logoff.ts` themselves (their exports are reused, not modified). No `infra/` or GitHub Actions changes — this is an application-layer story.

### References

- [Source: _bmad-artifacts/planning/epics/epic-8-desktop-tablet-layout-correction.md#Story 8.1: Responsive Nav Chrome & Profile Menu] — story statement and acceptance criteria (verbatim origin, lines 12-37); also the epic-level framing (lines 3-10: no new FR, AD-17/AD-18 only, single-epic rationale, story sequencing).
- [Source: _bmad-artifacts/planning/epics/requirements-inventory.md#UX-DR9, UX-DR19, UX-DR27, FR-33] — full amended text of the nav-chrome breakpoint variant (UX-DR9), the 660px column rule (UX-DR19), the Profile menu spec (UX-DR27), and FR-33's Story 8.1 extension note (lines 54, 120, 130, 135).
- [Source: _bmad-artifacts/planning/ux-designs/ux-energy-tracker-2026-08-08/EXPERIENCE.md#Responsive & Platform] — the authoritative UX narrative (lines 22-38): breakpoint value, nav-chrome swap, Profile menu contents/behavior, card-hierarchy note (not this story's concern).
- [Source: _bmad-artifacts/planning/ux-designs/ux-energy-tracker-2026-08-08/DESIGN/components.md#Nav chrome, #Profile menu] — component-level spec (lines 58-66): exact active-state token values/hexes, breakpoint-variant framing ("one component, not two"), Profile menu's avatar/dropdown contents.
- [Source: _bmad-artifacts/planning/ux-designs/ux-energy-tracker-2026-08-08/DESIGN/layout-spacing.md] — `{spacing.breakpoint-wide}` = 660px definition and rationale (line 9).
- [Source: _bmad-artifacts/planning/ux-designs/ux-energy-tracker-2026-08-08/mockups/critique-desktop-breakpoint-2026-09-23.html] — rendered before/after reference for every screen; Section 1 (Dashboard) for the top-nav shape, Section 7 (lines ~874-924) for the Profile dropdown shown open with real markup/CSS (`.top-nav`, `.profile-dropdown-wrap`, `.profile-btn`, `.profile-dropdown`, `.profile-dropdown-item` — lines 132-315 for the CSS, lines 895-924 for the open-state markup). Treat literal hex/rgba values here as a visual reference to reconcile against this app's real tokens (Dev Notes above), not to copy-paste.
- [Source: _bmad-artifacts/implementation/1-12-logoff-account-switching.md] — the logoff flow this story reuses; read in full before Task 4. Establishes the dialog step names, `checkPendingReadingsBeforeLogoff` contract, `navigateToLogout`'s full-page-navigation requirement, and the Household-scoped offline-queue fix from its own code review.
- [Source: _bmad-artifacts/planning/architecture/architecture-energy-tracker-2026-08-09/ARCHITECTURE-SPINE/invariants-rules.md#AD-17] — authoritative architecture decision for session/logoff; its FR-33 extension paragraph is why Task 4 must not touch `/logout`'s backend mechanics at all.
- [Source: src/EnergyTracker.Application/HouseholdClaimTypes.cs] — `ResolveDisplayName`'s raw-claim-first pattern that Task 5's `ResolveEmail` must mirror; read in full, includes the Story 3.6 `ClaimTypes.Name` bug explanation in its own doc comment.
- [Source: src/EnergyTracker.Api/Endpoints/HouseholdEndpoints.cs:58-60] — existing call-site pattern for `HouseholdClaimTypes.ResolveDisplayName(user)`, to mirror for `ResolveEmail(user)` in `SessionEndpoints.cs`.
- [Source: src/EnergyTracker.Api/Endpoints/SessionEndpoints.cs] — target file for Task 5; current `SessionResponse` shape and handler signature.
- [Source: tests/EnergyTracker.Application.Tests/HouseholdClaimTypesTests.cs] — exact 5-case test pattern to mirror for `ResolveEmail` (Task 7).
- [Source: web/src/App.tsx:15-21,41,166,284-330] — current `SessionResponse` interface, `supportsFederatedLogout` state pattern to mirror for `email`, and the per-page prop-drilling this story extends to 4 pages instead of 1.
- [Source: web/src/components/dashboard/nav-chrome.tsx] — target file for Task 2; current bottom-tab-bar-only implementation, `ACTIVE_CLASSNAME`, and the `active`/`onXClick` prop contract every page already calls it with.
- [Source: web/src/components/dashboard/nav-chrome.test.tsx] — existing test conventions to extend (Task 7).
- [Source: web/src/components/settings/settings-page.tsx] — current home of the entire logoff dialog state machine; Task 4 extracts from here without changing its rendered behavior (AC #5).
- [Source: web/src/components/ui/select.tsx] — closest existing Radix-floating-primitive analog for authoring `dropdown-menu.tsx` (Task 3) in this codebase's house style.
- [Source: web/src/lib/glass-classnames.ts] — `GLASS_MODAL_CLASSNAME`/`GLASS_SHEET_CLASSNAME` and their rationale comment; reuse for the Profile dropdown panel per Task 3's radius note.
- [Source: web/src/lib/logoff.ts] — `checkPendingReadingsBeforeLogoff`; reuse directly inside the new shared hook, do not reimplement.
- [Source: web/src/index.css:7-93] — the `@theme inline { ... }` block Task 1 extends; existing `--color-*`/`--radius-*`/`--spacing-*` custom-token precedent to follow for `--breakpoint-wide`.
- [Source: web/src/locales/en-US/translation.json:40-57, 272-277] — existing `settings.logoff.*` keys to reuse verbatim, and `dashboard.nav.*` keys for the existing nav-label pattern.
- [Source: web/e2e/app-shell.spec.ts] — this repo's one existing Playwright spec/convention to extend for Task 7's viewport-resize assertion.
- [Source: _bmad-artifacts/implementation/deferred-work.md:305] — pre-existing, still-open gap: no focus/accessibility management across the logoff dialog's steps. Not this story's job to fix, but be aware a second logoff entry point doesn't need to duplicate that gap unnecessarily if it's cheap to avoid.
- [Source: _bmad-artifacts/project-context.md#Critical Don't-Miss Rules, Process gates] — the two hard gates this story triggers: (1) no shipped-code fix merges without a linked spec/story doc (n/a here — this is itself the linked story); (2) mandatory live Auth0/Chrome verification before review→done for any OIDC-claims or browser-dependent story (Task 8).

## Dev Agent Record

### Agent Model Used

Claude Sonnet 5 (claude-sonnet-5)

### Debug Log References

- Live-verification session (Task 8, 2026-09-26): local Postgres (Docker) + API (`scripts/run-api.sh`) + Vite dev server, driven via Claude-in-Chrome against `https://localhost:5173`.
- Found and fixed a real live-only bug during Task 8(a): `Program.cs`'s `AddOpenIdConnect` never requested the `email` scope (default scope is `openid`/`profile` only) — Auth0 (per the OIDC spec) omits the email claim entirely from both the ID token and the userinfo response without it, so `/api/session`'s `Email` came back `null` even though `HouseholdClaimTypes.ResolveEmail`'s raw-claim-first logic was correct. Fixed with `options.Scope.Add("email")`. Re-verified live after the fix: `/api/session` returned the real test-user's `test@dummy.com`. Distinct root cause from Story 3.6's `ClaimTypes.Name` mapping gap — same failure mode (claim missing at `/api/session`), different mechanism (scope never requested, vs. raw-key-vs-mapped-claim-type mismatch).
- Manual `resize_window` (Claude-in-Chrome) did not resize the tab's actual content viewport in this sandbox (`window.innerWidth` stayed fixed regardless of requested width — an environment/window-manager limitation, not a code defect). Substituted with `web/e2e/app-shell.spec.ts`'s new Playwright spec, which drives a real (non-jsdom) Chromium instance via `page.setViewportSize()` and passed at both 375×800 and 900×800 — this is the actual regression guard Task 7 calls for regardless.
- The `npx shadcn add dropdown-menu` CLI (Task 3) repeated a previously-fixed alias-misresolution bug (epic-2 action item) in a new form: it wrote a broken `import { cn } from "cn"` (a real, unrelated npm package) instead of `@/lib/utils`, and added that spurious `cn` package to `package.json`/`package-lock.json`. Fixed the import and reverted the spurious dependency before proceeding; the file itself landed at the correct path (`web/src/components/ui/dropdown-menu.tsx`) this time.

### Completion Notes List

- **AC #1** (top nav renders at ≥660px with identical active-state tokens): `web/src/components/dashboard/nav-chrome.tsx`'s `hidden wide:flex` top-nav variant reuses `ACTIVE_CLASSNAME` (`bg-nav-chrome-active-bg`/`text-nav-chrome-active-foreground`) verbatim. Proven by `nav-chrome.test.tsx` (both variants assert the active classes) and live Chrome verification at native window width (~1372px) showing the top nav rendered correctly with brand wordmark, 4 links, and Profile menu.
- **AC #2** (bottom tab bar unchanged at <660px): `wide:hidden` added to the existing bottom-tab-bar `<nav>`, which also gained a `data-slot="nav-chrome-bottom"` hook and lost its own `mt-auto` (moved to a new wrapping `<div>`) to support Task 2's `order-first` repositioning — the rendered result at <660px is unchanged, but the markup itself is not byte-for-byte identical. Proven by `nav-chrome.test.tsx` (existing assertions retained, now scoped to the bottom variant), `web/e2e/app-shell.spec.ts`'s Playwright spec at a real 375×800 viewport, and — following code review (2026-09-26) — genuine live Claude-in-Chrome verification of both breakpoint sides against the real dev stack: a tab opened at 500px `window.innerWidth` renders only the bottom tab bar (no top nav/Profile menu); a tab opened at 1000px renders only the top nav with a working Profile menu. See Review Findings for the sandbox workaround (resize a new tab before navigation, rather than resizing an already-rendered one).
- **AC #3** (Profile dropdown shows email, Profile, Log off): `profile-menu.tsx`'s `DropdownMenuContent` renders the email (when present) via `DropdownMenuLabel`, a disabled `Profile` row, and a destructive-styled `Log off` row. Proven by `profile-menu.test.tsx` and live Chrome verification: opened the dropdown against the real `e2e-tester` test account and saw `test@dummy.com` / "Profile" / "Log off" rendered exactly as specified, after fixing the OIDC email-scope bug above.
- **AC #4** (Log off from the dropdown runs the identical Story 1.12 flow): `use-logoff.ts` is the single extracted state machine both `SettingsPage` and `ProfileMenu` consume; `ProfileMenu` mounts its own `<Dialog>` reusing `GLASS_MODAL_CLASSNAME` and the exact `settings.logoff.*` i18n keys. Proven by `use-logoff.test.ts`, `profile-menu.test.tsx`, and a full live run: opened the dropdown, clicked Log off, confirmed, and followed the redirect all the way through the real identity provider's own RP-initiated-logout confirmation page (greeting the real test-user by email) and back to a fresh login screen.
- **AC #5** (Settings' existing Logoff control unchanged): `settings-page.tsx` was refactored to consume `useLogoff` internally; its trigger label, dialog copy, and step sequence are untouched. Proven by `settings-page.test.tsx`'s pre-existing "Logoff control" test suite passing unmodified (only the internal wiring changed, per the task's own instruction).
- **Open question carried forward** (Task 3): the "Profile" dropdown row has no defined destination anywhere in the PRD/epics/UX docs — rendered as a visibly present, disabled row for this story. A future story needs to decide what it should do (a dedicated Profile/account page? nothing, ever?).
- Full backend (`dotnet test` equivalent, run directly against the built test binaries — `EnergyTracker.Application.Tests`: 393/393, `EnergyTracker.Api.Tests`: 224/224, `EnergyTracker.Architecture.Tests`: 5/5) and frontend (`vitest run`: 396/396, `tsc -b`, `oxlint`, `playwright test e2e/app-shell.spec.ts`: 2/2) suites all pass clean.

### File List

**Backend:**
- `src/EnergyTracker.Application/HouseholdClaimTypes.cs` — modified: new `ResolveEmail(ClaimsPrincipal)`
- `src/EnergyTracker.Api/Endpoints/SessionEndpoints.cs` — modified: `SessionResponse` gains `Email`; handler takes `ClaimsPrincipal user`
- `src/EnergyTracker.Api/Program.cs` — modified: OIDC options now request the `email` scope (live-verification fix, Debug Log)
- `tests/EnergyTracker.Application.Tests/HouseholdClaimTypesTests.cs` — extended: `ResolveEmail` 5-case pattern
- `tests/EnergyTracker.Api.Tests/TestAuthHandler.cs` — modified: new `EmailHeader`/`email` claim support
- `tests/EnergyTracker.Api.Tests/EnergyTrackerApiFactory.cs` — modified: `CreateAuthenticatedClient` gains an `email` parameter
- `tests/EnergyTracker.Api.Tests/SessionAndHouseholdCreationTests.cs` — extended: `Email` field coverage (present/absent/persists-with-household)

**Frontend:**
- `web/src/index.css` — modified: new `--breakpoint-wide: 660px` in `@theme inline`
- `web/src/App.tsx` — modified: `SessionResponse`/state gain `email`; threaded to all 4 pages
- `web/src/hooks/use-logoff.ts` — new: extracted shared logoff state machine
- `web/src/hooks/use-logoff.test.ts` — new
- `web/src/components/ui/dropdown-menu.tsx` — new: shadcn/radix-ui primitive
- `web/src/components/dashboard/nav-chrome.tsx` — modified: adds the `wide:flex` top-nav variant, `data-slot` markers, mounts `ProfileMenu`
- `web/src/components/dashboard/nav-chrome.test.tsx` — rewritten: both variants asserted
- `web/src/components/dashboard/profile-menu.tsx` — new
- `web/src/components/dashboard/profile-menu.test.tsx` — new
- `web/src/components/dashboard/dashboard-page.tsx` — modified: `supportsFederatedLogout`/`email` props
- `web/src/components/dashboard/dashboard-page.test.tsx` — updated call sites
- `web/src/components/trend-history/trend-history-page.tsx` — modified: `householdId`/`supportsFederatedLogout`/`email` props
- `web/src/components/trend-history/trend-history-page.test.tsx` — updated call sites
- `web/src/components/tariff/tariff-radar-page.tsx` — modified: same
- `web/src/components/settings/settings-page.tsx` — refactored to consume `useLogoff`; trigger/dialog UI unchanged (AC #5)
- `web/src/components/settings/settings-page.test.tsx` — updated call sites (assertions unchanged)
- `web/src/lib/glass-classnames.ts` — modified: new `GLASS_DROPDOWN_CLASSNAME`
- `web/src/locales/en-US/translation.json` / `de-DE/translation.json` — modified: new `profileMenu.*` keys
- `web/src/App.test.tsx` — updated call sites for ambiguous nav-button queries
- `web/e2e/app-shell.spec.ts` — extended: viewport-resize breakpoint assertion

**Story tracking:**
- `_bmad-artifacts/implementation/sprint-status.yaml` — status updates
