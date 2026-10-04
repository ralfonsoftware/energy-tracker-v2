# Epic 10: Deferred-Work Hardening

Closes the three items the 2026-10-02 `deferred-work.md` triage (`implementation/spec-deferred-work-triage.md`, Epic 8 retro action #6) promoted to their own stories. Each was deferred during a code review as "real, but out of that story's scope", and each affects something live: production migrations against real household data, the correlation shown on Events, and keyboard focus order on tablet/desktop. Delivers no new FR; hardens behavior that FR-17, NFR (data safety) and WCAG 2.4.3 already require.

**Origin:** Epic 8 retrospective (2026-10-02), action item #6 triage; promotions approved by Ralf the same day.

**FRs covered:** none (no new FR) — operationalizes FR-17 (correlation correctness) and the accessibility intent of FR-34/FR-35 (Epic 8 layout).
**Architecture:** AD-2 (dual-provider migrations), AD-6/AD-7 (background jobs, recompute call sites), AD-8, AD-10, AD-12 (Epic 10.2); no architecture change.

Stories are independent of each other and of Epic 9; they can ship in any order. Each carries an "Ask First" list of design decisions for Ralf, to be answered before `dev-story` starts.

## Story 10.1: Migration Safety on Deploy — Restore Point, Rollback Runbook, Expand/Contract Rule

See `implementation/10-1-migration-safety-on-deploy.md` (acceptance criteria live in the story file).

## Story 10.2: Event Correlation Forward-Window Recompute

See `implementation/10-2-event-correlation-forward-window-recompute.md`.

## Story 10.3: NavChrome Document Order Matches Visual Order (Focus Order, WCAG 2.4.3)

See `implementation/10-3-navchrome-dom-order-tab-order.md`.
