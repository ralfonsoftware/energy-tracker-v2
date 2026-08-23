# Epic 2: Meter Reading & Pattern Detective Status Core

The product's non-negotiable core loop: a Household member logs a Meter Reading in under a minute (with offline queuing), sets a Yearly Baseline, and sees a single trustworthy Status (within range / below baseline / trending) on the dashboard — computed from a gap-tolerant rolling baseline, with meter-rollover/reset regressions caught and classified rather than silently corrupting the pace. Fully functional with zero Smart Plug coverage. Realizes UJ-1 and UJ-2's Status half.

**FRs covered:** FR-1, FR-2, FR-3, FR-6, FR-7, FR-25, FR-28 (extension — re-parenting only; FR-28's core CRUD remains Epic 1), FR-30 (added post-Epic-2-retro 2026-08-18, extending Story 2.5's Status card), FR-31 (added 2026-08-23, dedicated Meter Reading history/browse surface — Story 2.8 shipped it as a standalone page; per the Epic 3 retro 2026-08-23, that surface is absorbed into Epic 4's Story 4.1 going forward — see `epic-4-trend-history-per-plug-insight.md`)
**NFRs:** NFR1 (perf tier 1), NFR7 (offline capture), NFR8 (audit trail on corrections — first wired in Story 2.8), NFR9 (recomputation policy), NFR10 (concurrency), NFR15 (says-less discipline)
**Architecture:** AD-4, AD-7, AD-12, AD-14, AD-16
**UX-DRs:** UX-DR1 (status/brand tokens), UX-DR2 (Status card), UX-DR3 (Log Reading sheet), UX-DR4 (Meter Regression prompt), UX-DR8 (primary action button), UX-DR9 (nav chrome), UX-DR11 (liquid glass elevation), UX-DR13 (one-level-deep modal stacking), UX-DR14 (empty/edge states), UX-DR15 (motion contract), UX-DR16 (accessibility floor), UX-DR17 (voice/tone), UX-DR18 (regression micro-flow)

## Story 2.1: Yearly Baseline Configuration

As a Household member,
I want to set and later edit my Household's Yearly Baseline,
So that Pattern Detective has a target to measure my consumption pace against.

**Acceptance Criteria:**

**Given** onboarding or Settings
**When** I set a Yearly Baseline
**Then** household-size presets (1 person ≈ 1500 kWh, 2 ≈ 2500 kWh, 3 ≈ 3500 kWh, 4 ≈ 4250 kWh) are offered as starting suggestions, never silently applied as a default (FR-2, AD-15)

**Given** an existing Yearly Baseline
**When** I change it
**Then** the change takes effect going forward only — it never retroactively rewrites past Status history (FR-2, NFR9)

**Given** the Yearly Baseline value
**When** stored
**Then** it is a Household-scoped config row, never a literal in code (AD-15)

**Given** two Household members editing the Yearly Baseline at the same time
**When** both submit
**Then** the second writer receives a 409 conflict rather than silently overwriting the first (AD-4, NFR10)

## Story 2.2: Meter Reading Entry with Offline Queueing

As a Household member,
I want to log a Meter Reading with today's date/time pre-selected in under a minute, even without connectivity,
So that I can capture my meter's number as part of my routine without breaking the habit.

**Acceptance Criteria:**

**Given** the Log Reading sheet
**When** it opens
**Then** today's date/time is pre-selected and editable, I enter a single kWh number, and save with one confirmation tap (FR-1)

**Given** the default path with no edits
**When** I save
**Then** confirmation lands in under a minute, responding within the ≤2s Tier 1 target (FR-1, NFR1)

**Given** I already logged a reading today
**When** I log a second reading later the same day with a different timestamp
**Then** it's accepted as a distinct entry — never rejected as a duplicate or silently overwritten (FR-1)

**Given** I enter a reading with an earlier timestamp than my most recent one
**When** I save it
**Then** it's accepted as a backfill, not rejected (FR-1)

**Given** no network connectivity at the meter
**When** I save a reading
**Then** it queues locally (IndexedDB) and syncs automatically when connectivity returns (NFR7)

**Given** a queued offline reading whose sync retries after losing its acknowledgment
**When** the retry lands
**Then** the API's idempotency-key upsert treats it as a no-op against the already-recorded reading — never a duplicate insert (AD-16)

**Given** a genuinely new reading entered while an earlier sync is still pending
**When** both eventually sync
**Then** both are recorded as distinct readings — the idempotency mechanism never collapses a legitimate second entry (AD-16)

**Given** the Log Reading sheet
**When** presented
**Then** it renders as a sheet over the Dashboard, never its own route, with top-rounded/flush-bottom shape and a tabular-nums kWh field (UX-DR3)

**Given** the Log Reading sheet
**When** it opens
**Then** it traps focus while open and returns focus to the triggering control on close, is announced on open (role + label) for screen readers, and its Save action meets the 44×44pt-equivalent tap-target minimum (UX-DR16)

## Story 2.3: Meter Reading Regression Detection & Classification

As a Household member,
I want a Meter Reading lower than the previous one to be flagged and classified rather than silently breaking my baseline,
So that a meter swap or digit rollover doesn't corrupt my Status.

**Acceptance Criteria:**

**Given** a new Meter Reading lower than the Reading immediately preceding it by timestamp (not entry order)
**When** it's saved
**Then** a classification prompt opens asking to confirm *reset* or *rollover*, and no negative consumption rate is computed (FR-25)

**Given** the classification prompt
**When** I confirm *reset*
**Then** a new baseline-computation sequence starts going forward without discarding prior Reading history (FR-25)

**Given** the classification prompt
**When** I confirm *rollover*
**Then** the interval's consumption is computed as (meter's known digit capacity − previous Reading) + new Reading (FR-25)

**Given** a backfilled Reading
**When** compared for regression
**Then** "immediately preceding" is determined by timestamp order, never entry order (FR-25)

**Given** an unconfirmed regression prompt
**When** any later reading or Status computation runs
**Then** the flagged Reading is excluded from baseline computation until resolved, and the prompt never silently expires or defaults to either classification (FR-25)

**Given** a second lower-than-previous Reading arrives while an earlier regression prompt is still open
**When** it's detected
**Then** it queues behind the first rather than opening a second, conflicting prompt — at most one open `MeterRegressionPrompt` exists per Main Meter (FR-25, AD-12)

**Given** the regression prompt triggers while a Log Reading sheet happens to be open
**When** it opens
**Then** it supersedes the Log Reading sheet rather than stacking on top of it (UX-DR13)

**Given** the regression prompt UI
**When** rendered
**Then** it uses the neutral/informational glass treatment, never destructive/error styling — this is a normal classification step, not a system error (UX-DR4, UX-DR18)

**Given** the regression prompt
**When** it opens
**Then** it traps focus while open and returns focus to the triggering control on close, and is announced on open (role + label) for screen readers (UX-DR16)

## Story 2.4: Gap-Tolerant Rolling Baseline & Status Computation

As a Household member,
I want the system to compute a single trustworthy Status from my reading pace vs my Yearly Baseline, tolerant of irregular reading gaps,
So that I know at a glance whether I'm on track.

**Acceptance Criteria:**

**Given** a sequence of Meter Readings with irregular intervals, including multi-day gaps
**When** the pace is computed
**Then** each gap is absorbed into the rate calculation between that reading pair rather than breaking or resetting the computation (FR-3)

**Given** the computed pace
**When** compared to the Yearly Baseline
**Then** the comparison is like-for-like — pace-to-date vs. baseline-to-date (FR-3)

**Given** an unusually long gap since the last reading
**When** Status is computed
**Then** it's flagged low-confidence rather than presented with the same certainty as a normal 1-2 day interval (FR-3)

**Given** the current pace exceeds the Yearly Baseline pace by more than the household's configured threshold (default ~100 kWh)
**When** Status is computed
**Then** it resolves to *trending* (FR-6)

**Given** the pace is exactly equal to the baseline pace plus the threshold
**When** Status is computed
**Then** it resolves to *within range*, not *trending* — an exact tie resolves to the calmer state (FR-6)

**Given** fewer than two Meter Readings exist, or no Yearly Baseline is set
**When** Status is requested
**Then** it is undefined rather than defaulting to any of the three states (FR-6)

**Given** a new Meter Reading is saved
**When** the save completes
**Then** Status recomputes immediately — never on a fixed schedule alone (FR-6, AD-7)

**Given** Status is (re)computed
**When** the computation completes
**Then** the result is also written to an immutable `StatusSnapshot` row via the single `IStatusRecomputeService`, so a later Yearly Baseline/threshold edit never rewrites this historical value (AD-7, NFR9)

**Given** any Status computation or API response
**When** inspected
**Then** no Smart Plug or Event data is summed into or reconciled against the Main Meter-derived pace figure — `MeterReading` is the sole authoritative total (AD-14)

**Given** a Reading excluded due to an unresolved regression prompt (Story 2.3)
**When** Status is computed
**Then** that Reading, and everything chronologically after it, is excluded from the computation until the prompt is resolved

## Story 2.5: Dashboard Status Display

As a Household member,
I want to see my current Status as the first thing on the Dashboard,
So that I know if I'm on track without hunting for the answer.

**Acceptance Criteria:**

**Given** the Dashboard
**When** it loads
**Then** the Status is visible without scrolling or drilling into a sub-view (FR-7)

**Given** the Status card
**When** rendered
**Then** no chart is required to read it — it's legible as a single glanceable state: status dot, uppercase badge, headline sentence, supporting sentence (FR-7, UX-DR2)

**Given** first-ever load with no computable Status (fewer than two Readings or no Yearly Baseline, per Story 2.4)
**When** the Dashboard renders
**Then** it shows an onboarding prompt ("log your first reading to get started") rather than blank space or a default Status value (FR-7, UX-DR14)

**Given** the three real Status states (within range / below baseline / trending)
**When** rendered
**Then** each uses its dedicated AA-verified badge-text token (never the raw status-triad hex) and its own status color — never the brand-accent teal, and never a 4th "unknown" visual treatment (UX-DR1, UX-DR2)

**Given** the Status card
**When** rendered in Dark and Light mode
**Then** both render the rear/front glass panel stack with backdrop blur+saturate as equal citizens — Dark shows the glow/specular treatment, Light substitutes frosted-white translucency with soft green-tinted drop shadows rather than attempting to replicate the Dark-mode glow (UX-DR11)

**Given** the Status card's data resolves, on cold load or after a recompute
**When** it appears
**Then** it plays the settle + specular-sweep entrance animation once, gated behind `prefers-reduced-motion: no-preference`, with a fully settled/instant fallback when reduced motion is requested (UX-DR15)

**Given** a Status recompute happens while the Dashboard is open
**When** the value changes
**Then** it's announced via `aria-live="polite"` rather than requiring a manual refresh check (UX-DR16)

**Given** the Dashboard's cold load
**When** data hasn't resolved yet
**Then** a shadcn `Skeleton` matching the Status card's footprint is shown so nothing reflows on resolution (UX-DR14)

**Given** the Status headline/body copy
**When** rendered
**Then** it follows the plain-language voice/tone discipline — named number, named thing that happened, never generic congratulation or gamified language (UX-DR17)

**Given** the Status card and any other Dashboard element
**When** compared
**Then** the Status card remains the single highest-visual-weight surface on the Dashboard — nothing else visually competes with it (NFR15)

**Given** the Dashboard
**When** rendered
**Then** it includes the primary "Log Reading" action button (pill shape, gradient fill) that opens the Log Reading sheet from Story 2.2; its press state compresses to ~0.965 scale with shadow pull-in, never a color flash (UX-DR8)

**Given** the bottom tab bar (mobile)
**When** the Dashboard is the active surface
**Then** its nav item uses the brand-accent-tinted active state, never a status color; the tab bar shell carries all four top-level entries (Dashboard, Trend History, Tariff Radar, Settings) per UX-DR9, with the latter three surfaces' content filled in by later epics

## Story 2.6: Room / Power Point / Device Re-parenting

As a Household member,
I want to move a Power Point to a different Room, or a Device to a different Power Point,
So that I can reorganize my Household's tagging structure once real day-to-day use shows it no longer matches how the house is actually laid out or used.

**Acceptance Criteria:**

**Given** an existing Power Point
**When** I move it to a different Room
**Then** its Room assignment is reassigned going forward — the one deliberate exception to the init-only immutability Room/PowerPoint/Device otherwise follow (deferred in Story 1.9, reintroduced here) (FR-28)

**Given** an existing Device
**When** I move it to a different Power Point
**Then** its Power Point assignment is reassigned going forward, following the same reassignment rule as a Power Point move (FR-28)

**Given** a Power Point or Device with Smart Plug readings or Events already recorded against it before the move
**When** the move completes
**Then** those historical rows keep displaying the Room/Power Point/Device identity snapshotted at write time — the move is never retroactively applied to past data (AD-10)

**Given** a Room, Power Point, or Device that is archived (soft-deleted)
**When** I attempt to move a child into it, or move it as a child of another archived parent
**Then** the move is rejected — an archived node can't become a new attachment point

**Given** a Power Point or Device I'm moving
**When** I select a destination
**Then** only non-archived Rooms (for a Power Point) or non-archived Power Points (for a Device) within the same Household are offered — cross-Household reassignment is never possible (FR-28, AD-3)

**Given** a move I've just made
**When** I view the tagging scaffold immediately after
**Then** the moved item appears under its new parent and is no longer listed under its old parent — never duplicated under both

**Given** the Room/Power Point/Device management surface in Settings (Story 1.9)
**When** a move is available
**Then** it's exposed as an additive "Move to…" action on the existing management UI rather than a new standalone surface — reusing the same list/detail pattern already established

## Story 2.7: Status Calculation Detail

As a Household member,
I want to open a details view from the dashboard Status card,
So that I understand how the "X kWh over/under expected" figure was actually calculated, not just told the number.

**Acceptance Criteria:**

**Given** the dashboard Status card
**When** I open its details view
**Then** it shows pace-to-date, baseline-to-date, the elapsed period baseline-to-date covers, the difference between them (the figure already shown on the card), and the household's configured trending threshold (FR-30)

**Given** the details view
**When** rendered
**Then** it shows only the aggregate figures already computed for Status — never a list of individual contributing Meter Readings (FR-30, confirmed with Ralf: a full reading list would be long with little added value)

**Given** the Status card's low-confidence flag (Story 2.4) is active
**When** I open the details view
**Then** it explains why — stale last reading, not corroborated by Smart Plug coverage — rather than surfacing the flag with no explanation (FR-30, FR-6)

**Given** the details view
**When** rendered
**Then** no chart is required — it's legible as labeled figures, same as the Status card itself (FR-30, FR-7)

**Given** Status is undefined (fewer than two Readings, or no Yearly Baseline set — Story 2.4/2.5's onboarding empty state)
**When** the dashboard is in that state
**Then** no details view is offered — there is no calculation yet to explain (FR-30, FR-6)

**Given** the existing `/api/status` endpoint
**When** the details data is served
**Then** it's exposed via a separate endpoint, never merged into the Status response — consistent with the codebase's existing drill-down convention (e.g. Trend History, FR-8)

## Story 2.8: Meter Reading History View

As a Household member,
I want a dedicated, browsable list of every Meter Reading I've logged,
So that I can find and correct a specific past entry, distinct from just seeing the aggregate trend.

**Acceptance Criteria:**

**Given** the Meter Reading History view
**When** I open it
**Then** it lists individual Meter Readings (value + timestamp) for the Main Meter, ordered by timestamp — not entry order, consistent with FR-1/FR-25's sequencing (FR-31)

**Given** Trend History (FR-8) and Status Calculation Detail (FR-30)
**When** comparing surfaces
**Then** this view is the only place raw, per-Reading data is browsable — both of those stay aggregate-only, unchanged by this story (FR-31, FR-8, FR-30)

**Given** a Reading in the list
**When** I open it to correct a mis-logged value
**Then** editing preserves the original value as a visible correction note rather than a silent overwrite — this story is the first to wire the shared audit-trail mechanism (NFR8) into a Meter Reading edit path (FR-31, NFR8)

**Given** a Reading currently under an open, unconfirmed regression classification (Story 2.3 / FR-25)
**When** it appears in the list
**Then** it's visibly flagged as pending rather than shown as a normal confirmed entry (FR-31, FR-25)

**Given** the existing `/api/meter-readings` POST endpoint
**When** the history list is served
**Then** it's exposed via a new paginated GET on the same route, following the codebase's existing kebab-case-plural route convention (FR-31, AD-consistency-conventions)

> **Superseded (2026-08-23):** the ACs above describe what Story 2.8 actually shipped as a standalone page and remain accurate history — but per the Epic 3 Retro's Significant Discovery, this surface (including the "Trend History stays aggregate-only" claim above) is consolidated into Epic 4's Story 4.1 going forward, and the Dashboard's "History" text-link is removed once that lands. See `epic-4-trend-history-per-plug-insight.md` for the current, forward-looking definition.
