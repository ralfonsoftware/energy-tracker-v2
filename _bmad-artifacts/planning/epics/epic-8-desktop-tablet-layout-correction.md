# Epic 8: Desktop & Tablet Layout Correction

Every existing frontend surface (Dashboard, Trend History, Tariff Radar, Settings) renders correctly at desktop and tablet width (≥660px) instead of an unconstrained mobile layout — one shared breakpoint, a top-nav chrome variant, a Profile menu, and system-wide application of already-established-but-under-used patterns (unit-inside-field, card-hierarchy, section grouping, tree summary rows). Delivers no new domain capability; makes every existing capability (Epics 1, 2, 4, 5) usable at desktop/tablet width, closing the gap between the already-documented UX-DR19 responsive-layout intent and what actually shipped. Single epic rather than per-screen epics: all seven UX-DRs share one breakpoint mechanism and one nav-chrome/profile-menu component reused identically across all four screens — splitting by screen would repeatedly touch the same shared component. Story sequencing: shared breakpoint infrastructure + nav chrome + profile menu first, then Dashboard → Trend History → Tariff Radar → Settings.

Source: UX review of all 9 production screens at desktop and tablet width, 2026-09-23 — see `_bmad-artifacts/planning/ux-designs/ux-energy-tracker-2026-08-08/mockups/critique-desktop-breakpoint-2026-09-23.html` for the full before/after mockups and rationale behind every acceptance criterion below.

**FRs covered:** none (no new FR) — additive touch on FR-33 only (Story 1.12, status: done; Story 8.1 below extends its reachable surface, does not modify its existing behavior)
**NFRs:** none directly; closes a gap the Cross-Cutting NFRs never specified (no existing responsive/breakpoint NFR)
**Architecture:** AD-17 (session/logout — Story 8.1 reuses the existing `/logout` flow verbatim), AD-18 (i18n — any new UI string is a resource-file addition to both `en-US`/`de-DE`, never a code branch)
**UX-DRs:** UX-DR9 (amended), UX-DR19 (amended), UX-DR23, UX-DR24, UX-DR25, UX-DR26, UX-DR27 — full text of the amendments and new entries is in `epics/requirements-inventory.md`

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
