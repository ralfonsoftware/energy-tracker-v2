# Sprint Change Proposal — Energy Tracker

**Date:** 2026-08-16
**Raised by:** Amelia (Dev), on Ralf's request to review Sally's new UX mockups before continuing Epic 2
**Mode:** Batch

---

## 1. Issue Summary

Sally (UX) rendered dedicated HTML mockups for every Epic 1–3 surface (`_bmad-artifacts/planning/ux-designs/ux-energy-tracker-2026-08-08/mockups/`) after seeing the live app's Dashboard-nav-stub and Settings screens render as flat, unstyled shadcn button lists. Investigating why revealed the mockups aren't the actual gap — the **glass-panel component layer they describe was never built**:

- `web/src/index.css` wires only the base shadcn brand-color delta (`--primary`, `--background`, `--foreground`, dark+light — confirmed correct via grep). Zero component-level tokens exist: no `surface-glass`, no backdrop-blur, no status-badge pairs, no `rounded` scale, no nav chrome.
- Every screen built so far — `household-creation-form.tsx`, `invite-generate-panel.tsx`, `settings-page.tsx`, `tagging-scaffold-manager.tsx` (Epic 1); `yearly-baseline-form.tsx`, `log-reading-sheet.tsx` (Epic 2) — is plain stock shadcn `<Button>`/`<Input>` in a flex column.
- This was never an oversight. Story 2.2's and Story 2.3's own Dev Notes **explicitly and correctly** scope it out each time: *"No glass-panel tokens are wired yet... use plain shadcn Dialog defaults... flag if a real design/token pass happens later."* Nobody ever scheduled that pass.
- Story 2.3 (**`ready-for-dev` right now**, baseline commit predates the mockups) is about to repeat the pattern for the Meter Regression prompt — even though `mockups/key-log-reading-flow.html` Frame 2 now renders that exact component.

**Discovered:** 2026-08-16, during Amelia's review of the new mockups at Ralf's request, specifically to answer "how do we get these implemented before we build too much more without them."

---

## 2. Impact Analysis

### Epic Impact
- **Epic 1 (done):** functionally complete and correct; visually does not match the now-final DESIGN.md/mockups for Settings, Household Invite, Room/Power Point/Device management. Needs a retrofit pass, not rework.
- **Epic 2 (in-progress):** 2.1/2.2 done, same visual gap as Epic 1 (Yearly Baseline form uses full-text preset buttons + separate unit label — both superseded by Sally's redesign). **2.3 (`ready-for-dev`) is the actual blocking point** — its Task 6 currently instructs building the Meter Regression prompt as a bare `Dialog`, which would be a third instance of the same deferred debt.
- **Epic 3 (backlog):** unaffected in scope — its 5 Smart Plug Import states are already fully mocked (`key-smart-plug-import.html`) and will consume the new component set directly once built.
- **Epics 4–7 (backlog):** unaffected; out of scope for this change.
- No epic is invalidated. No new epic is needed — this is story-level, not epic-level.

### Story Impact
- **New story required**, sequenced between 2.2 (done) and 2.3 (ready-for-dev): wire the component-level tokens + build the shared components + retrofit the 6 existing screens.
- **Story 2.3 needs amendment** before `dev-story` runs on it (see Section 4).
- **2.4, 2.5, 2.6** (backlog) are not modified, but will benefit — 2.5 (Dashboard) in particular needs almost the full component set at once (Status card, primary button, nav chrome, empty state), and building it during 2.5 instead of now would mean doing this work twice.

### Artifact Conflicts
- **PRD:** none. No functional requirement is affected; MVP scope is unchanged.
- **Architecture:** none. `DESIGN.md`/`EXPERIENCE.md` already define every token and component this change consumes — no new architecture decision (AD-*) is needed. Purely additive frontend work.
- **UX Design:** none needed beyond what Sally already produced — the mockups and spine are the source of truth this story builds against.
- **Other artifacts:** new Vitest + Testing Library unit tests for each shared component (existing convention). No CI/CD, IaC, or backend changes.

---

## 3. Recommended Approach

**Selected: Option 1 — Direct Adjustment (insert one story, amend one story).**

| Option | Viable? | Effort | Risk | Notes |
|---|---|---|---|---|
| **1. Direct adjustment** — insert a Design System Foundation story ahead of 2.3, amend 2.3 | ✅ Viable | Medium | Low | Additive only; no data model, API, or architecture change |
| **2. Rollback** 2.1/2.2 | ❌ Not viable | — | — | Their logic is correct and tested; only the visual layer needs a retrofit, not a revert |
| **3. MVP scope review** | ❌ Not applicable | — | — | This is visual-consistency debt, not a scope or requirements question |

**Rationale:** the component gap has now repeated three times (Epic 1, 2.1/2.2, and was about to happen again in 2.3) with the identical documented excuse. Building the shared component layer once, retrofitting the 6 screens that need it today, and amending 2.3 to consume it is cheaper than letting 2.4–2.6 and Epic 3 each defer it again — especially since 2.5 (Dashboard) would otherwise have to build most of this same set from scratch anyway. Confirmed directly with Ralf (2026-08-16) as the preferred path over "amend 2.3 only" or "foundation now, retrofit later."

---

## 4. Detailed Change Proposals

### 4.1 New Story — placement and scope

**Not a numbered PRD story** (no epic file defines it as an FR-backed story) — same category as Epic 1's retrospective action items *"Wire DESIGN.md's real design tokens..."* and *"Sweep existing Epic 1 UI... to use the real design tokens"* (both already closed at base-token scope only; this story is the deferred continuation of exactly those two items, now covering the component layer they explicitly scoped out).

Proposed ID/placement: **`2-2b-design-system-foundation`**, sequenced in `sprint-status.yaml` between `2-2-meter-reading-entry-with-offline-queueing` and `2-3-meter-reading-regression-detection-classification`. The `b` suffix avoids renumbering 2.3–2.6 (which epic-2's file and story 2.3's own file already reference by number) while keeping strict execution order visible.

**Story (draft, for `bmad-create-story` to expand into full tasks/AC):**

> As a developer,
> I want the DESIGN.md-described glass-panel component layer wired into the codebase as shared, reusable components,
> so that every screen built from here on — starting with Story 2.3's regression prompt — renders against the real design system instead of deferring it again.

**Scope:**
1. `web/src/index.css` — full component-level token set for both dark and light, sourced from `DESIGN/colors.md`, `DESIGN/elevation-depth.md`, `DESIGN/shapes.md`, `DESIGN/typography.md`, `DESIGN/layout-spacing.md` (concrete hex/rgba values already exist there and in the mockups — no new design decisions, pure transcription): `surface-glass`, `surface-panel-back`, `specular-sweep`, status triad (`status-within-range/-below-baseline/-trending`) + their AA-verified badge-text pairs, `rounded-sm/md/lg/full`, `spacing.card-padding/card-gap/mobile-margin`.
2. Shared components (`web/src/components/ui/` or a new `web/src/components/glass/`): `GlassCard`, a glass Sheet/Dialog shell (bottom-sheet rounded-top/flush-bottom variant reused by Log Reading + Household Invite; centered-modal variant reused by Meter Regression + Room Management), `StatusBadge`, the primary pill/gradient action button, the bottom-tab nav chrome, the household-size icon-preset control, and the unit-inside-field number input pattern (all per `mockups/key-*.html` — pixel/token reference, not reinterpretation).
3. Retrofit to consume the new components: `household-creation-form.tsx`, `invite-generate-panel.tsx`, `settings-page.tsx`, `tagging-scaffold-manager.tsx`, `yearly-baseline-form.tsx` (incl. the icon-preset + unit-in-field redesign), `log-reading-sheet.tsx` — matching `key-onboarding.html`, `key-household-invite.html`, `key-settings.html`, `key-room-management.html`, `key-log-reading-flow.html` screen-by-screen.
4. Unit tests per new shared component (Vitest + Testing Library, existing convention); existing screen tests updated for the new markup, not rewritten.

**Explicitly out of scope for this story:** the Dashboard (2.5, not yet built), Trend History/Tariff Radar/Smart Plug Import screens (Epics 3–5, not yet built) — the shared components land here, but wiring them into not-yet-existing screens is each future story's own job.

### 4.2 Story 2.3 amendment

File: `_bmad-artifacts/implementation/2-3-meter-reading-regression-detection-classification.md`

**Task 6, bullet 2 (component build instruction):**

```diff
- Add `web/src/components/meter-reading/meter-regression-prompt-dialog.tsx`: shadcn `Dialog` (not
- `Sheet` — the UX spec explicitly calls for a Dialog here), receiving the open prompt (or `null`)
- and a resolve callback as controlled props from `App.tsx`... **No glass-panel/backdrop-blur
- tokens exist in `web/src/index.css` yet** (`{colors.surface-glass}` etc. are unwired — confirmed
- by grep; same gap Story 2.2's Dev Notes flagged for the Log Reading sheet) — use the shadcn
- `Dialog` default styling as-is; this already satisfies "neutral/informational, never destructive"
- (AC #8) since it's the plain default treatment, not `variant="destructive"` styling.
+ Add `web/src/components/meter-reading/meter-regression-prompt-dialog.tsx` using the glass
+ centered-modal shell from Story 2.2b's Design System Foundation (not a bare shadcn `Dialog`),
+ matching `mockups/key-log-reading-flow.html` Frame 2 exactly: neutral glass-panel treatment,
+ two plain-labeled choice actions, no status triad or destructive color. Receives the open prompt
+ (or `null`) and a resolve callback as controlled props from `App.tsx`, same as before.
```

**Dev Notes** — remove the now-stale line:

```diff
- **No glass-panel tokens are wired yet** (`{colors.surface-glass}`, backdrop-blur, etc.) —
- confirmed by grep against `web/src/index.css`; only the base shadcn delta
- (background/foreground/primary/ring/font) from the Epic-1 retro action item is wired. This is
- the same gap Story 2.2 hit for the Log Reading sheet. Use plain shadcn `Dialog` defaults for the
- regression prompt — this already satisfies "neutral, not destructive" (no `destructive` variant
- anywhere in this component), it just isn't the literal glass-panel treatment
- `DESIGN/components.md` describes. Flag if a real design/token pass happens later, same as 2.2 did.
+ **Glass-panel tokens are wired as of Story 2.2b** (Design System Foundation) — use the shared
+ centered-modal shell and `StatusBadge`/token set it provides, not a bare shadcn `Dialog`. See
+ `mockups/key-log-reading-flow.html` Frame 2 for the exact rendered reference.
```

**References** — add:

```diff
+ [Source: _bmad-artifacts/planning/ux-designs/ux-energy-tracker-2026-08-08/mockups/key-log-reading-flow.html#Frame-2] — rendered reference for the Meter regression prompt's exact glass-modal treatment.
+ [Source: _bmad-artifacts/implementation/2-2b-design-system-foundation.md] — the shared glass Sheet/Dialog shell and StatusBadge components this story now builds against.
```

No other task in Story 2.3 changes — the domain/application/API layer (Tasks 1–5) and test scope (Task 7) are unaffected; this is purely a Task 6 presentation-layer amendment.

### 4.3 `sprint-status.yaml` update

```diff
   epic-2: in-progress
   2-1-yearly-baseline-configuration: done
   2-2-meter-reading-entry-with-offline-queueing: done
+  2-2b-design-system-foundation: backlog
   2-3-meter-reading-regression-detection-classification: ready-for-dev
```

(`backlog`, not `ready-for-dev` — per this file's own status definitions, `ready-for-dev` means the story file exists in `_bmad-artifacts/implementation/`; that's `bmad-create-story`'s job in Section 5 Step 2, not this proposal's.)

Plus a dated log-header note (matching the file's existing convention):

```
# 2026-08-16: story 2-2b-design-system-foundation inserted ahead of 2.3 (sprint-change-proposal-2026-08-16.md)
#   — closes the glass-panel component gap Stories 2.2/2.3 both deferred; retrofits Epic 1 + 2.1/2.2 screens
```

---

## 5. Implementation Handoff

**Scope classification: Moderate** — backlog reorganization (new story insertion, `sprint-status.yaml` edit, amendment to an existing `ready-for-dev` story) plus direct implementation. No PM/Architect involvement needed (no PRD or architecture change).

| Step | Owner | Action |
|---|---|---|
| 1 | Ralf | Approve this proposal |
| 2 | Amelia (`bmad-create-story`) | Expand the Section 4.1 scope sketch into `_bmad-artifacts/implementation/2-2b-design-system-foundation.md` with full AC/tasks, same rigor as 2.1–2.3 |
| 3 | Amelia (`bmad-dev-story`) | Implement 2.2b: tokens, shared components, retrofit, tests |
| 4 | Amelia | Apply the Section 4.2 diff to Story 2.3's file |
| 5 | Amelia (`bmad-dev-story`) | Resume 2.3 with the amended Task 6 |
| 6 | Amelia | Apply the Section 4.3 `sprint-status.yaml` diff now (tracking), update statuses as 2.2b/2.3 progress |

**Success criteria:** `web/src/index.css` contains the full component-token set (dark+light); the 7 shared components exist and are unit-tested; all 6 existing screens visually match their corresponding `mockups/key-*.html` file; Story 2.3's regression prompt is built against the shared modal shell, not a bare `Dialog`; no regression in Story 2.1/2.2's existing passing test suites.
