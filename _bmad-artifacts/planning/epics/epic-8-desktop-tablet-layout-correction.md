# Epic 8: Desktop & Tablet Layout Correction

Every existing frontend surface (Dashboard, Trend History, Tariff Radar, Settings) renders correctly at desktop and tablet width (≥660px) instead of an unconstrained mobile layout — one shared breakpoint, a top-nav chrome variant, a Profile menu, and system-wide application of already-established-but-under-used patterns (unit-inside-field, card-hierarchy, section grouping, tree summary rows). Delivers no new domain capability; makes every existing capability (Epics 1, 2, 4, 5) usable at desktop/tablet width, closing the gap between the already-documented UX-DR19 responsive-layout intent and what actually shipped. Single epic rather than per-screen epics: all seven UX-DRs share one breakpoint mechanism and one nav-chrome/profile-menu component reused identically across all four screens — splitting by screen would repeatedly touch the same shared component. Story sequencing: shared breakpoint infrastructure + nav chrome + profile menu first, then Dashboard → Trend History → Tariff Radar → Settings.

Source: UX review of all 9 production screens at desktop and tablet width, 2026-09-23 — see `_bmad-artifacts/planning/ux-designs/ux-energy-tracker-2026-08-08/mockups/critique-desktop-breakpoint-2026-09-23.html` for the full before/after mockups and rationale behind Stories 8.1–8.5's acceptance criteria. **Stories 8.10–8.12 (added 2026-09-30)** add the Theme Toggle (FR-35) and Language Toggle (FR-34) to the Profile menu and, below 660px, to a Settings Preferences card — UX per `mockups/key-profile-preferences.html`. **Stories 8.6–8.9 (added 2026-09-28)** extend the epic with a follow-up round, sourced from Ralf's live desktop review of the shipped Stories 8.1–8.5 output — the 660px column, while a correct fix for the "ungoverned mobile layout on desktop" problem, itself left a desktop viewport under-used once seen live (a squeezed Trend History chart, long single-column Settings/Tariff lists). See `mockups/key-trend-history.html`, `mockups/key-settings.html`, and `mockups/key-tariff-radar.html` (all promoted 2026-09-28) for the corrected state.

**FRs covered:** FR-34, FR-35 (added 2026-09-30 — Language Toggle, Theme Toggle; stories to be added); additive touch on FR-33 only (Story 1.12, status: done; Story 8.1 below extends its reachable surface, does not modify its existing behavior)
**NFRs:** none directly; closes a gap the Cross-Cutting NFRs never specified (no existing responsive/breakpoint NFR)
**Architecture:** AD-17 (session/logout — Story 8.1 reuses the existing `/logout` flow verbatim), AD-18 (i18n — any new UI string is a resource-file addition to both `en-US`/`de-DE`, never a code branch)
**UX-DRs:** UX-DR9 (amended), UX-DR19 (amended twice — 2026-09-23 and 2026-09-28), UX-DR23, UX-DR24, UX-DR25, UX-DR26, UX-DR27, UX-DR28, UX-DR29, UX-DR30, UX-DR31, UX-DR32, UX-DR33 — full text of the amendments and new entries is in `epics/requirements-inventory.md`

## Story 8.1: Responsive Nav Chrome & Profile Menu

As a Household member on a tablet or desktop browser,
I want the navigation to appear at the top instead of the bottom, and a central Profile menu to reach Log off,
So that I don't have to reach the bottom edge of a wide screen or scroll through Settings to sign off.

**Acceptance Criteria:**

**Given** a Household member opens the app at an available width ≥660px
**When** the page loads
**Then** the navigation (Dashboard/Trend History/Tariff Radar/Settings) renders as a horizontal bar at the top instead of the bottom tab bar, using the identical active-state tokens the bottom tab bar already uses (UX-DR9)

**Given** the same view at an available width <660px
**When** the page loads
**Then** the existing bottom tab bar renders unchanged — no regression for mobile

**Given** the top nav is visible (≥660px)
**When** the member clicks the avatar button on the right
**Then** a dropdown opens showing the account email, a "Profile" entry, and a "Log off" entry

**Given** the profile dropdown is open
**When** the member clicks "Log off"
**Then** the exact same logoff flow from Story 1.12 starts (offline-queue check, federated-logout warning where applicable, navigation to `/logout`) — no new logoff logic, only a second entry point (UX-DR27, FR-33)

**And** the existing Logoff control on the Settings page (<660px) remains unchanged

## Story 8.2: Dashboard Desktop/Tablet Layout

As a Household member,
I want the Dashboard at ≥660px to show a clearly bounded column with a sensibly placed action button,
So that the Status card doesn't look stretched across the full window and "Log reading" doesn't float in empty space.

**Acceptance Criteria:**

**Given** the Dashboard is displayed at an available width ≥660px
**When** the page loads
**Then** the Status card, Tariff Check prompt, and the primary action button are constrained to a centered 660px-wide column instead of stretching to the full window width (UX-DR19)

**And** the "+ Log reading" button renders directly following the Status/Tariff-Check cards, not floating in the remaining screen area

**Given** the two header icon buttons (History, Import)
**When** rendered at ≥660px
**Then** each carries a visible short-word label in addition to its icon ("History", "Import") instead of a tooltip only

**And** at <660px the existing mobile layout (including icon-only buttons) remains unchanged

## Story 8.3: Trend History Desktop/Tablet Layout

As a Household member,
I want Meter Readings and Events in Trend History at ≥660px shown as a dense list without wasted space,
So that I can see more entries at a glance instead of a table with a wide dead column.

**Acceptance Criteria:**

**Given** the Trend History page is displayed at an available width ≥660px
**When** the page loads
**Then** the trend chart, the Meter Readings list, the Events list, and the Room → Power Point → Device card are constrained to the 660px column (UX-DR19)

**Given** the Meter Readings or Events list is expanded
**When** rendered at ≥660px
**Then** table rows show value, date, and the Edit action without a wide unused gap between date and action (prior finding: ~40% of row width was dead space)

**Given** the Room → Power Point → Device card (pure reference display, no interaction)
**When** rendered at ≥660px
**Then** it uses the "quiet" card tier instead of the "glass" tier still used by, e.g., the Meter Readings list (which has an Edit action) (UX-DR24)

## Story 8.4: Tariff Radar Desktop/Tablet Layout

As a Household member,
I want the Tariff forms at ≥660px to use compact, paired input fields,
So that I don't see a single number field stretched across the whole screen with its unit far from the value.

**Acceptance Criteria:**

**Given** the "Add Tariff" form is displayed at ≥660px
**When** the page loads
**Then** Monthly Base Fee and Price per kWh render as composed value+unit fields (unit-inside-field), side by side in one row instead of each alone on full width (UX-DR23)

**Given** the "Compare Tariff" form is displayed at ≥660px
**When** the page loads
**Then** Candidate Base Fee, Candidate Price/kWh, and Switching Bonus use the same paired unit-inside-field pattern

**Given** the "Your current Tariff" and "Candidate Tariff" result cards (pure reference values, no input)
**When** rendered at ≥660px
**Then** they use the "quiet" card tier; the "Is it worth switching?" verdict card with the signal rows remains in the "glass" tier (UX-DR24)

**And** all cards are constrained to the 660px column (UX-DR19)

## Story 8.5: Settings Desktop/Tablet Layout

As a Household member,
I want Settings at ≥660px organized into clearly labeled sections, with "Invite a member" in a place that makes sense,
So that I don't have to guess why an unheaded button sits below the room list.

**Acceptance Criteria:**

**Given** the Settings page is displayed at ≥660px
**When** the page loads
**Then** content is organized into labeled sections: "Yearly Baseline", "AI Plausibility Check", "Household", "Rooms, Power Points & Devices", "Data" (UX-DR25)

**And** "Invite a member" renders as a regular row inside the "Household" section, not as a standalone unheaded button below the room list

**Given** the household-size presets in the "Yearly Baseline" section
**When** rendered at ≥660px
**Then** they use the already-established compact `hh-preset` sizing instead of the currently oversized boxes

**Given** the Room tree in the "Rooms, Power Points & Devices" section
**When** a level (Room or Power Point) is expanded
**Then** parent levels show a chevron + item-count summary (e.g. "Living Room — 5 Power Points") instead of equally-weighted bordered buttons at every depth (UX-DR26)

**And** "Add Power Point"/"Add Device" renders as a text link at the end of the expanded level, not a persistently-rendered filled pill

## Story 8.6: Wide-Column Increase Across All Surfaces

As a Household member on a desktop or wide tablet browser,
I want the app's content column to use more of the available width,
So that a surface like Trend History's chart doesn't feel squeezed into a narrow strip while the rest of the window sits empty.

**Acceptance Criteria:**

**Given** any of the four surfaces (Dashboard, Trend History, Tariff Radar, Settings) at an available width ≥660px
**When** the page loads
**Then** the content column's max-width is 900px instead of 660px, on all four surfaces alike — still one shared rule, not a per-surface exception (UX-DR19)

**And** the 660px breakpoint itself is unchanged as the trigger for the phone→wide layout switch and the nav-chrome swap (Story 8.1) — only the column's own max-width changed

**Given** an available width between 660px and 900px
**When** the page loads
**Then** the column simply fills the available width up to the 900px cap (unchanged `w-full`/`max-w` mechanics) rather than sitting at a fixed 660px with unused gutters on either side

**Given** the Dashboard's Status card at ≥900px
**When** rendered
**Then** its own proportions are unchanged from Story 8.2 — it simply sits in a column with slightly more margin either side, since nothing about its content changed

## Story 8.7: Meter Readings, Events & Tariff History Entry-Grid

As a Household member reviewing Meter Readings, Events, or Tariff History on a wide screen,
I want individual entries arranged in a multi-column grid instead of one long single-column list,
So that I can see more entries at a glance instead of scrolling through a narrow list beside empty space.

**Acceptance Criteria:**

**Given** the Meter Readings list, Events list (Trend History), or Tariff History list (Tariff Radar), expanded, at an available width ≥660px
**When** rendered
**Then** individual entries reflow into a `repeat(auto-fill, minmax(260px, 1fr))` grid — 2 or 3 tiles per row depending on available width, never a fixed count — instead of a single-column table (UX-DR28)

**And** each tile shows the same content its table row showed (primary value/description, secondary detail lines, timestamp/period, a Pending/Current badge where applicable, the Edit trigger) — reflowed, not redesigned or reduced

**Given** the same three lists at an available width <660px
**When** rendered
**Then** the existing single-column list is unchanged

**Given** the Room → Power Point → Device tree (Trend History) and the Tariff Radar form fields
**When** rendered at ≥660px
**Then** neither is gridded by this story — the tree is addressed by Story 8.8, the form fields are already paired per Story 8.4/UX-DR23

## Story 8.8: Settings Rooms/Power Points Grid-with-Expand & Section Pairing

As a Household member with several Rooms and Power Points,
I want the Rooms/Power Points list to use the available width and short Settings sections to sit side by side,
So that I don't scroll through a long column of one-line collapsed rows while most of the screen sits empty.

**Acceptance Criteria:**

**Given** the "Rooms, Power Points & Devices" tree at an available width ≥660px
**When** Room and Power Point rows are collapsed
**Then** they render in a `repeat(auto-fill, minmax(220px, 1fr))` grid instead of a single-column list (UX-DR29)

**Given** a Room or Power Point tile in that grid
**When** it is expanded
**Then** it spans the full grid width (`grid-column: 1 / -1`) and reveals its children inline below the summary row — the same `details`/`summary` accordion behavior as Story 8.5, just reflowed while collapsed

**Given** Device rows (the leaf level, carrying Story 2.6's drag-to-reorder handle)
**When** rendered at any width
**Then** they remain a plain single-column list, never gridded — dragging to reorder across a 2D grid is a materially more ambiguous gesture than dragging down a list

**Given** the "AI Plausibility Check" and "Household" sections at ≥660px
**When** rendered
**Then** they sit side by side in one row instead of each alone on a full-width row (UX-DR30)

**And** "Yearly Baseline" and "Rooms, Power Points & Devices" are not paired with anything and stay full width

## Story 8.9: Tariff Comparison Side-by-Side Summary

As a Household member comparing tariffs,
I want the current and candidate tariff summaries shown side by side,
So that I can compare them at a glance instead of scrolling between two stacked cards.

**Acceptance Criteria:**

**Given** a computed Compare-Tariff result at an available width ≥660px
**When** rendered
**Then** the "Your current Tariff" and "Candidate Tariff" summary panels render side by side in two columns instead of stacked (UX-DR31)

**Given** the "Is it worth switching?" verdict/signal card
**When** rendered at ≥660px
**Then** it stays full width below both summary panels — it does not become a third column alongside them

**Given** the same screen at <660px
**When** rendered
**Then** the existing stacked layout (Story 8.4) is unchanged

## Story 8.10: Theme Toggle in the Profile Menu (System / Light / Dark)

*(added 2026-09-30, new FR-35)*

As a Household member,
I want to choose System, Light, or Dark from the Profile menu,
So that I can override my device's color scheme when I prefer the other theme, without changing my OS setting.

**Acceptance Criteria:**

**Given** the Profile menu is open (≥660px, Story 8.1)
**When** rendered
**Then** it shows, between the account email and the Profile / Log off rows, an "Appearance" row carrying the new Preference icon strip (UX-DR32) with exactly three segments — System, Light, Dark (monitor / sun / moon icons) — plus the scope sub-label "This device", with the dropdown widened to 296px; the active segment shows the fill, 1px accent border, and `aria-checked="true"` (never color alone), using the existing nav-chrome active tokens and the canonical focus-ring pair

**Given** the strip has keyboard focus
**When** the member presses ←/→ (or ↑/↓) or Home/End
**Then** focus moves **and selects** per `radiogroup` semantics, Tab enters the group on the checked segment, every segment has an accessible name (the System name includes the resolved value, e.g. "System — currently Dark"), and Escape closes the menu returning focus to the avatar button

**Given** the member selects Light or Dark
**When** the selection is made
**Then** the theme applies immediately on every screen without reload or network request, the menu stays open, and the choice is stored per device in `localStorage` — not on the Household, not per member (FR-35)

**Given** System is selected (the default when nothing is stored)
**When** the OS color-scheme preference changes
**Then** the app follows it live (today's `initColorScheme` behavior is preserved); with Light or Dark selected, OS changes are ignored

**Given** a stored choice exists
**When** the page loads or reloads (including after logoff)
**Then** the stored theme is applied before first paint — the inline script in `web/index.html` reads it, so there is no flash of the wrong theme — and the `<meta name="theme-color">` values follow the effective theme rather than only the OS media query

**Given** `localStorage` is unavailable or holds an unrecognized value
**When** the app loads
**Then** it falls back to System without error

**Given** the device is offline
**When** the member changes the theme
**Then** it works exactly as online (no server involvement)

**And** all new strings exist in both `en-US` and `de-DE` catalogs (AD-18); Dark and Light both remain fully designed on every surface (UX-DR11), verified in both themes for the new row (UX-DR32, FR-35)

**Technical notes:**
- New shared component: the Preference icon strip (`radiogroup`), reused by Stories 8.11 and 8.12.
- Extend `web/src/lib/color-scheme.ts` (currently OS-only, comment "No manual toggle yet") rather than adding a parallel mechanism; update its header comment.
- Reference: `_bmad-artifacts/planning/ux-designs/ux-energy-tracker-2026-08-08/mockups/key-profile-preferences.html` §1, §2.

## Story 8.11: Language Toggle — Household Locale Switch

*(added 2026-09-30, new FR-34)*

As a Household member,
I want to switch the household's language between Deutsch and English from the Profile menu,
So that the app's language and number/date formats match what the household actually wants, after creation as well as at creation.

**Acceptance Criteria:**

**Given** the Profile menu is open (≥660px)
**When** rendered
**Then** below the Appearance row it shows a "Language" row with the Preference icon strip (UX-DR32) offering `DE` and `EN` (endonyms `Deutsch` / `English` as accessible names — never translated, never flags) and the scope sub-label "Whole household"; the segment matching the persisted `Household.Locale` is selected

**Given** a member selects the other language
**When** the selection is made
**Then** the segment shows selected immediately with a saving indicator, the new `Household.Locale` is persisted through a new authenticated Household-scoped endpoint (any Household member may call it — no admin role exists in v1), and on success the UI language **and** number/date formatting switch together without reload (AD-18), the menu stays open, and a polite live region announces the change in the new language

**Given** the session/household response and the i18next instance
**When** the app loads for a signed-in member
**Then** the active UI language is `Household.Locale` — the browser-language detector no longer overrides it after Household creation; the detector remains only for pre-Household screens (Household creation display language), preserving Story 1.5's explicit-choice behavior; this also removes the language-vs-date-format mismatch recorded in `deferred-work.md` (Tariff Check card)

**Given** the persist request fails
**When** the failure is returned
**Then** the strip reverts to the persisted Locale, an inline error appears under the row ("Couldn't change the language…", rendered in the still-persisted language), and no UI language or format changes occur

**Given** the device is offline
**When** the Profile menu is opened
**Then** the Language strip is disabled and its sub-line reads "Needs a connection" (de: "Benötigt eine Verbindung"); the Appearance row is unaffected

**Given** another Household member has the app open
**When** the Locale is changed elsewhere
**Then** they receive the new Locale on their next load or session refresh — no live push is required

**Given** the Locale is changed
**When** stored data is inspected
**Then** it is unchanged (locale-neutral storage, NFR5) and any offline-queued Meter Reading (AD-16) is unaffected

**And** every new string exists in both catalogs (AD-18); the endpoint validates the Locale against the supported list (`de-DE`, `en-US`) and rejects anything else (FR-34, UX-DR32)

**Technical notes:**
- Session response already carries `Locale`; add the write endpoint alongside the existing `PUT /households/{id}/…` settings endpoints (e.g. `/households/{id}/locale`) and its API tests.
- Reference: mockup §1–§2 (saving / offline / error states).

## Story 8.12: Preferences Card in Settings (<660px)

*(added 2026-09-30, new FR-34 / FR-35)*

As a Household member on a phone,
I want the same Appearance and Language controls in Settings,
So that I can change them where I am, even though the Profile menu only exists at desktop/tablet width.

**Acceptance Criteria:**

**Given** the Settings screen at an available width <660px
**When** rendered
**Then** a "Preferences" glass card appears in the Account group (the "Account" section label, directly above the unchanged Log off control from Story 1.12), containing the Appearance and Language rows with the same strips, sub-labels, states, and behavior as Stories 8.10 and 8.11 (UX-DR33) — one shared component, no duplicated logic

**Given** the same screen at ≥660px
**When** rendered
**Then** the Preferences card is not rendered — the Profile menu is the single home for both controls at that width

**Given** the card at 320–340px width in both `de-DE` and `en-US`
**When** rendered
**Then** rows and sub-labels do not wrap or overflow, and segments keep their 44×44 hit area

**Given** a member changes theme or language in the card
**When** they then widen the viewport to ≥660px (or reload)
**Then** the Profile menu shows the same, persisted state (Theme per device, Language per Household)

**And** Story 1.12's Log off control and the Settings page's existing sections are otherwise unchanged; Dark and Light both verified (UX-DR33, FR-34, FR-35)

**Technical notes:** depends on Stories 8.10 and 8.11. Reference: mockup §3.
