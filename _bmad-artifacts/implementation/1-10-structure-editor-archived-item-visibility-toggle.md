---
baseline_commit: 9a1076d9a829771296561fedd2d9ff6dfa8ab5a1
---

# Story 1.10: Structure Editor Archived-Item Visibility Toggle

Status: done

<!-- Note: Validation is optional. Run validate-create-story for quality check before dev-story. -->

## Story

As a Household member,
I want to show or hide archived Rooms, Power Points, and Devices in the structure editor,
so that a tree that's accumulated a lot of soft-deleted history doesn't stay cluttered with items I no longer manage day-to-day.

## Acceptance Criteria

1. **Given** the Room/Power Point/Device management surface (Story 1.9), **when** it renders with no interaction, **then** archived items (`ArchivedAt` set, FR-28/AD-10) are **hidden from the tree by default** — this is an intentional change from the pre-Story-1.10 behavior (where archived items always rendered inline with an "Archived" badge, unconditionally), **decided and confirmed with Ralf on 2026-08-23**, overriding this epic's originally-drafted "default stays visible" text. Switching the toggle on reveals archived items inline with their "Archived" badge exactly as they rendered unconditionally before this story existed. See Dev Notes.
2. **Given** the management surface, **when** I switch the toggle to hide archived items (its default state), **then** Rooms, Power Points, and Devices with `ArchivedAt` set do not render in the tree at all — not just visually de-emphasized, actually absent.
3. **Given** the toggle set to hide archived items, **when** I switch it on to show them, **then** archived items appear inline with their "Archived" badge, exactly as they rendered unconditionally before this story existed.
4. **Given** the hide-archived toggle state, **when** it changes, **then** it is a view filter only — it never touches the underlying soft-delete state (`ArchivedAt`), never affects Story 2.6's reassignment/"Move to…" behavior, and never affects which items are offered as active-selection destinations (already excluded regardless of this toggle, per Story 1.9).
5. **Given** an archived parent (e.g. a Room) with non-archived children still nested under it, **when** the toggle hides archived items (its default state), **then** the non-archived children remain visible — hiding a parent does not cascade-hide children whose own `ArchivedAt` is unset (they're still reachable, matching this epic's "historical references still resolve correctly" guarantee, Story 1.9).
6. **Given** the toggle, **when** I leave and return to the management surface, **then** the toggle resets to its default (hide archived) — it does **not** persist across visits. **Decision confirmed with Ralf (2026-08-23) during story creation: no persistence** — the epic's open question is resolved; see Dev Notes for the reasoning.

## Tasks / Subtasks

- [x] Task 1: Frontend — i18n strings (AC #2, #3)
  - [x] Add to both `web/src/locales/en-US/translation.json` and `de-DE/translation.json`'s `taggingScaffold` block (keep key-set parity — Story 1.9/1.5/1.8/2.6's established discipline):
    - `hideArchivedToggle` — the control's `aria-label`, e.g. en: `"Hide archived items"`, de: `"Archivierte Elemente ausblenden"` (used when currently showing archived, i.e. this is what clicking it will do next — matches an `aria-pressed` toggle-button convention, not a static label).
    - `showArchivedToggle` — the same control's `aria-label` when currently hiding archived, e.g. en: `"Show archived items"`, de: `"Archivierte Elemente einblenden"`.
  - [x] No new `confirm*`/`error*` strings needed — this is a pure client-side view filter, no request, no failure mode.

- [x] Task 2: Frontend — toggle state and recursive visibility filtering (AC #1, #2, #3, #5)
  - [x] `web/src/components/tagging-scaffold/tagging-scaffold-manager.tsx`: add `const [showArchived, setShowArchived] = useState(false)` next to the existing `dialog`/`nameInput`/`submitting` state. **Default `false`** — archived items are hidden until the member explicitly turns them on (AC #1, decided with Ralf 2026-08-23 — this intentionally changes the pre-existing always-visible behavior from Story 1.9; see Dev Notes). No persistence (AC #6) — this is exactly why plain `useState` with no effect/storage wiring is correct here: it's supposed to reset on every mount.
  - [x] **The non-obvious part of this story: an archived Room/Power Point can have non-archived children nested under it** (Story 1.9 Dev Notes: `ArchiveRoom`/`ArchivePowerPoint` never cascade-archive their children). The current render is one `<details>` block per Room, containing its own `<summary>` (name + badge) *and* its nested Power Points/Devices in the same block (`tagging-scaffold-manager.tsx:469-594`) — you cannot simply skip an archived Room's entire `<details>` block when `!showArchived`, because that would cascade-hide any live, non-archived Power Points nested inside it, violating AC #5. Resolve this with a per-node three-way computation, applied at both the Room→PowerPoint and PowerPoint→Device level (same shape both times):
    ```ts
    const roomHasVisibleChildren = (room: RoomDto) =>
      (powerPointsByRoom.get(room.id) ?? []).some((pp) => showArchived || !pp.archivedAt)

    // for each room:
    const archived = !!room.archivedAt
    if (archived && !showArchived && !roomHasVisibleChildren(room)) {
      // fully absent — no DOM at all (the common case: an archived Room with no live children)
      continue
    }
    const suppressOwnRow = archived && !showArchived // has live children, so still renders, but its OWN name/badge row is gone
    ```
    When `suppressOwnRow` is true: render a plain wrapper (a `<div>`, not `<details>`/`<summary>` — an unlabeled `<details>` would show a default "Details" disclosure affordance, which leaks the hidden Room's presence and is worse UX than either fully hiding or fully showing it) containing only the filtered Power Point list, with **no** Room name, badge, rename/delete/add-Power-Point buttons for that Room — the Room's own row is genuinely absent, per AC #2's "not just visually de-emphasized, actually absent." When `suppressOwnRow` is false (the normal case — active Room, or archived-and-showing), render the existing `<details>`/`<summary>` block unchanged.
    Apply the identical three-way logic one level down for Power Point → Device (`archived Power Point with live Devices, hidden` → render only its filtered Device list with no Power Point summary row, no rename/delete/add-Device/move buttons for that Power Point).
    Devices have no children, so a Device only needs the simple two-way filter: `showArchived || !device.archivedAt`.
  - [x] This is a pure render-time filter over already-fetched `rooms`/`powerPoints`/`devices` state — **no new fetch, no new API call, no backend change** (confirmed: `GET /api/rooms`/`/api/power-points`/`/api/devices` already return archived rows today, Story 1.9 Task 2 — `ListRoomsAsync`/`ListPowerPointsAsync`/`ListDevicesAsync` have no `includeArchived` parameter and never will; this story only ever changes what the frontend chooses to render from a response it already has).
  - [x] Toggling `showArchived` must not touch `handleSubmit`/`handleDelete`/`handleMoveTo`, the `dialog` state, or any `fetch` call (AC #4) — it is wired into the render path only, next to the existing `roomsById`/`powerPointsByRoom`/`devicesByPowerPoint` derivations (`tagging-scaffold-manager.tsx:192-206`).
  - [x] **Move destinations and create-child pickers are unaffected by design, not by extra code.** `MoveDestinationList` (Move to… dialog, Story 2.6) and the create-Power-Point/create-Device buttons already filter on `!archivedAt` independently of any render-visibility toggle (Story 1.9 AC #4) — do not thread `showArchived` into either of those; AC #4 explicitly requires this toggle to leave that exclusion exactly as-is.

- [x] Task 3: Frontend — the toggle control itself (AC #1, #2, #3)
  - [x] Add a single icon-button toggle to the header row next to the existing `t('taggingScaffold.addRoom')` button (`tagging-scaffold-manager.tsx:458-461`) — **reuse the existing `Button` component** (`variant="ghost" size="icon"`, matching the Rename/Delete/Move icon buttons already in this file) with `lucide-react`'s `Eye`/`EyeOff` icon: render `EyeOff` while `showArchived` is `false` (the default — archived items currently hidden) and `Eye` once toggled to `true` (archived items currently shown) — the icon represents current state, not the action. `aria-pressed={showArchived}` (pressed = "show archived" is currently on). `aria-label`: `t('taggingScaffold.showArchivedToggle')` while `showArchived` is `false` (describes what clicking does next — reveal them), `t('taggingScaffold.hideArchivedToggle')` while `true`. **Do not add a new shadcn `Switch` primitive for this** — no `Switch`/`Toggle` component exists yet in `web/src/components/ui/` (only `badge`, `button`, `card`, `dialog`, `input`, `label`, `select`, `sheet`, `skeleton`, `unit-input`), and Story 1.9's own experience with the shadcn CLI in this sandboxed environment (silently failed / wrote to the wrong path, `dialog.tsx` was hand-authored instead) plus the density preference already established for this UI (compact icon controls over labeled pill/switch controls elsewhere in the tagging scaffold — Rename/Delete/Move are all icon buttons, not labeled buttons) both point at reusing `Button`+icon rather than introducing a new primitive and a new dependency for a single on/off control.
  - [x] `aria-pressed` (not a `role="switch"`/`aria-checked` pair) matches how a plain toggle `Button` communicates binary state per WAI-ARIA — this project has no existing switch-role component to follow instead.

- [x] Task 4: Verify against every AC
  - [x] Extend `web/src/components/tagging-scaffold/tagging-scaffold-manager.test.tsx` (mocked `fetch`, following the existing pattern — `mockFetchRoutes`, `jsonResponse`):
    - AC #1: with a mix of active and archived Rooms/Power Points/Devices seeded via the mocked `GET` responses, the tree renders with archived items **absent** by default (no toggle interaction) — assert `queryByText` returns `null` for an archived item's name with no prior toggle click. This is a change from whatever the existing "archived badge shows unconditionally" test (if any) currently asserts — update it, don't just add alongside it, since the old always-visible-by-default behavior no longer holds.
    - AC #2/#3: click the toggle → archived Rooms/Power Points/Devices (with no live children) appear with the badge; click again → they disappear entirely (assert `queryByText` returns `null`, not just a style/class assertion).
    - AC #4: toggling does not issue any `fetch` call beyond the initial three GETs (assert on the mock's call count before/after toggling), and does not change what a "Move to…"/create-child destination list offers (open a Move dialog before and after toggling, assert the same destination set both times).
    - AC #5: seed an archived Room with a non-archived Power Point nested under it (and, separately, an archived Power Point with a non-archived Device under it); with the toggle at its default (hide archived), assert the live Power Point (and live Device) still renders, while the archived Room's own name/badge and the archived Power Point's own name/badge do not.
    - AC #6: no persistence test needed (there is nothing to persist) — optionally assert the toggle's initial state is "hide archived" on a fresh render, covering AC #1's default.
  - [x] **Three existing tests in this file will fail under the new default and need updating, not just new tests added alongside them** (found during story creation — see Dev Notes): `archiving a Room shows the archived badge and hides the add-Power-Point action` (currently line 93), `archiving a Power Point shows the archived badge and hides the add-Device action` (line 204), and `archiving a Device shows the archived badge` (line 288) each archive an item and then immediately assert `screen.findByText('Archived')` — under the new hide-by-default behavior that item is no longer rendered at all after archiving, so each of these three tests needs a toggle-on click (via the new control's `aria-label`, Task 3) inserted between the archive action and the `findByText('Archived')` assertion.

- [x] Task 5: Documentation
  - [x] No `docs/*.md` changes expected — no new operator-facing configuration surface, no new migration, no new adapter/env var, no backend change at all (same conclusion Story 1.9's Task 10 and Story 2.6's Task 7 reached, for the same reason: this story is entirely inside one existing frontend component). Confirm this still holds once implementation is done; update only if something unexpected surfaces.

### Review Findings

- [x] [Review][Patch] Cascade-visibility bug: an archived Room whose only Power Point is also archived but has a live Device drops the entire Room from the tree, hiding the live Device [web/src/components/tagging-scaffold/tagging-scaffold-manager.tsx:213-215,495] — fixed: `roomHasVisibleChildren` now recurses through `powerPointHasVisibleChildren`; regression test added.
- [x] [Review][Patch] Story file/sprint-status.yaml claim the epic file was left unedited by this story, but the same commit does modify it [_bmad-artifacts/implementation/1-10-structure-editor-archived-item-visibility-toggle.md:82,108] — fixed: both files corrected to state the epic's AC text was updated in place.
- [x] [Review][Patch] Duplicated Room-wrapper Tailwind class string repeated verbatim in both the suppressed-row `<div>` branch and the normal `<details>` branch, no shared constant [web/src/components/tagging-scaffold/tagging-scaffold-manager.tsx] — fixed: extracted `ROOM_ROW_BORDER_CLASS` module-level constant, used by both branches.
- [x] [Review][Patch] Task 4 asked for round-trip toggle coverage at Room/Power-Point/Device level; only a Room-level round-trip test was added [web/src/components/tagging-scaffold/tagging-scaffold-manager.test.tsx:539-561] — fixed: added Power-Point-level and Device-level round-trip tests.
- [x] [Review][Defer] No test exercises the German (`de-DE`) toggle strings specifically — every assertion in the suite hardcodes English, a pre-existing whole-file test convention this diff inherits rather than introduces [web/src/components/tagging-scaffold/tagging-scaffold-manager.test.tsx] — deferred, pre-existing

## Dev Notes

- **This story has zero backend surface.** No Domain/Application/Infrastructure/Api/migration changes — `GET /api/rooms`/`/api/power-points`/`/api/devices` already return every row, archived and active alike (Story 1.9 Task 2's explicit design: `List*Async` has no `includeArchived` parameter because "AC #4's 'excluded from active-selection pickers' is a frontend filtering concern, not a backend query concern"). This story is the frontend half of that same "always fetch everything, decide what to show client-side" design that Story 1.9 already put in place — it needs no new port method, no new use case, no new endpoint, no new DTO field.
- **The one genuinely hard part of this story is AC #5's cascade rule, and it's easy to get wrong by treating "hide archived Rooms" as "filter the top-level `rooms` array before mapping."** That naive approach (`rooms.filter(r => showArchived || !r.archivedAt).map(...)`) would silently cascade-hide any non-archived Power Point nested under an archived-but-not-cascade-archived Room, because in the current component (`tagging-scaffold-manager.tsx:469-594`) a Room's own `<details>` block and its children's DOM live inside the same JSX block — there's no separate "children" render path to fall back to. Task 2 spells out the three-way per-node logic (fully absent / present-but-own-row-suppressed / present-normally) needed to satisfy this without an incorrect naive filter. This is exactly the kind of thing Story 1.9's own children (an already-archived Room keeping fully-functional, independently-manageable Power Points) makes possible in practice, not just in theory — Story 1.9's Dev Notes explicitly chose non-cascading archive for this reason, so the scenario this AC covers is a real, reachable state, not a hypothetical edge case.
- **Open Question resolved — confirmed with Ralf 2026-08-23, during this story's creation (two follow-up questions, not assumed):** (1) no persistence — plain `useState` component-local state, resets on every mount (page load / navigating away from and back to Settings), no per-member or per-Household persisted preference; (2) **the default itself changes from "show archived" to "hide archived"** — this is a deliberate reversal of the epic's originally-drafted AC #1 text (`epic-1-foundation-deployment-household-access.md#Story 1.10`, which read "this story adds a toggle to control that, it does not change the underlying default"). The story file's AC #1/#2/#3/#6 above already reflect the confirmed, hide-by-default behavior; the epic file's AC #1/#2/#3/#6 text was also updated in place (same commit) to match, so both documents now agree — see the epic file's own AC text rather than relying on this note alone.
  - **No-persistence reasoning:** this codebase has no existing mechanism for persisting an arbitrary UI-only preference — no `localStorage`/`sessionStorage` usage exists anywhere for user prefs (AD-17 already forbids client-storing anything auth-adjacent, reinforcing the instinct not to reach for browser storage casually here), and there's no Household-scoped "UI settings" table to extend either (AD-15's Household-scoped-config rows are for business-meaningful values like baselines/thresholds/currency, not view-filter state). Building either mechanism for a single boolean toggle would repeat the disproportionate-infrastructure-for-one-story pattern Story 1.9's Dev Notes already rejected once for Settings routing.
  - **Concrete consequence of the default flip — do not miss this:** three existing tests in `tagging-scaffold-manager.test.tsx` (lines 93, 204, 288 as of this story's baseline commit) archive an item and then immediately assert the "Archived" badge is visible. Under the new default those items go invisible immediately after archiving, so all three now need an explicit toggle-on step inserted before their badge assertion — see Task 4's callout. This is exactly the kind of regression a naive "just flip the default" edit would introduce silently (tests would fail, correctly, the moment this change lands) — treat a red run of these three specific tests after implementing Task 2/3 as expected and fix them per Task 4, not as a sign something else is wrong.
- **AC #4 needs no new code to satisfy, only care not to accidentally regress it.** `MoveDestinationList` (Story 2.6) and the create-Power-Point/create-Device buttons' archived-parent exclusion (Story 1.9) are both already independent of any render-level "show archived" state — they filter on `!archivedAt` directly off the full `rooms`/`powerPoints`/`devices` arrays, not off whatever this story's new toggle happens to be showing. Do not thread `showArchived` into either of those two call sites; keep them reading from the unfiltered arrays exactly as today.
- **Constraints that still apply, unchanged:** AD-10 (this story's entire reason for existing — the "Archived" badge and non-cascading archive it displays), AD-18 (the two new i18n strings, Task 1, go through the catalog like every other string in this file — no inline literals), NFR3/NFR4 (unaffected — no new route, no new query).

### Project Structure Notes

New/modified files this story introduces — a single frontend file, plus its test and both locale catalogs:

```text
energy-tracker-v2/
  web/
    src/
      locales/de-DE/translation.json, en-US/translation.json  # modified — taggingScaffold.hideArchivedToggle/showArchivedToggle keys
      components/
        tagging-scaffold/
          tagging-scaffold-manager.tsx                        # modified — showArchived state, recursive visibility filter, toggle button
  tests/
    web/src/components/tagging-scaffold/tagging-scaffold-manager.test.tsx  # modified — AC #1-#5 coverage
```

No changes expected anywhere under `src/EnergyTracker.Domain/`, `EnergyTracker.Application/`, `EnergyTracker.Infrastructure/`, `EnergyTracker.Infrastructure.Migrations.*/`, or `EnergyTracker.Api/` — see Dev Notes ("zero backend surface").

### References

- [Source: _bmad-artifacts/planning/epics/epic-1-foundation-deployment-household-access.md#Story 1.10] — story statement and acceptance criteria origin; also the epic header line (`FR-26, FR-27, FR-28, FR-29`) confirming FR-29 is this story's covered requirement. **Note:** the epic's own AC #1 text and its "confirm with Ralf before implementation" open-question note on persistence were resolved 2026-08-23 and the epic file's AC text was updated in place in the same commit to match this story file's ACs and Dev Notes — both documents agree on the default-visibility and persistence points.
- [Source: _bmad-artifacts/planning/epics/requirements-inventory.md#FR-29] — FR-29's testable consequences: "toggle state controls whether archived Rooms/Power Points/Devices render in the tree at all... toggling hide-archived does not affect the underlying soft-delete/reassignment behavior — it's a view filter only, not a data change"
- [Source: _bmad-artifacts/planning/prds/prd-energy-tracker-2026-08-08/prd/4-features.md#FR-29] — PRD feature entry; confirms this is a follow-up backfilled 2026-08-22, no PRD text on persistence (basis for treating persistence as this story's own decision, resolved above)
- [Source: ...ARCHITECTURE-SPINE/invariants-rules.md#AD-10] — historical tag integrity: soft-delete only, no cascade; the reason an archived Room can still have live, independently-manageable Power Points under it (AC #5's premise)
- [Source: ...ARCHITECTURE-SPINE/invariants-rules.md#AD-15] — no hardcoded Household-specific values; cited in Dev Notes to explain why a persisted Household-scoped "UI preference" row would be a mismatch for this story's decision, not a fit
- [Source: _bmad-artifacts/implementation/1-9-room-power-point-device-management.md] — previous-but-one story: the entire `TaggingScaffoldManager` component this story extends; its Dev Notes on non-cascading archive (`ArchiveRoom`/`ArchivePowerPoint` don't cascade) are the direct cause of AC #5's requirement; its Task 2 note that `List*Async` returns all rows unfiltered is why this story needs no backend change; its "no react-router, local view state, proportionate infrastructure" reasoning is the precedent this story's persistence decision follows
- [Source: _bmad-artifacts/implementation/2-6-room-power-point-device-re-parenting.md] — immediately previous structure-editor story: current `TaggingScaffoldManager` shape this story modifies (`DialogState` union, `MoveDestinationList`, `roomsById`/`powerPointsByRoom`/`devicesByPowerPoint` derivations at lines 192-206); confirms Move destination filtering is independent of any render-visibility state, which this story must not disturb (AC #4)
- [Source: web/src/components/tagging-scaffold/tagging-scaffold-manager.tsx] — current implementation (747 lines) this story modifies directly; specifically the Room/Power Point/Device render block (lines 469-594) whose one-`<details>`-per-node-with-nested-children shape is why AC #5 needs the three-way per-node logic in Task 2, and the header row (lines 456-461) the new toggle button attaches to
- [Source: web/src/components/tagging-scaffold/tagging-scaffold-manager.test.tsx] — existing `mockFetchRoutes`/`jsonResponse` mocked-`fetch` Vitest/Testing-Library pattern this story's new AC coverage extends
- [Source: web/src/locales/en-US/translation.json, de-DE/translation.json] — existing `taggingScaffold.*` key set (verified current content) this story extends by two keys, parity discipline unchanged
- [Source: web/src/components/ui/] — confirmed current shadcn primitives (`badge`, `button`, `card`, `dialog`, `input`, `label`, `select`, `sheet`, `skeleton`, `unit-input`) — no `switch`/`toggle` exists yet, basis for Task 3's decision to reuse `Button`+icon instead of adding one
- [Source: src/EnergyTracker.Api/Endpoints/TaggingScaffoldEndpoints.cs] — confirmed `GET /rooms`/`/power-points`/`/devices` call `ListRoomsAsync`/`ListPowerPointsAsync`/`ListDevicesAsync` directly with no archived-filtering parameter, verifying "zero backend surface" for this story

## Dev Agent Record

### Agent Model Used

Claude Sonnet 5 (claude-sonnet-5), via `bmad-dev-story`.

### Debug Log References

None — implementation went green on first full test run after fixing one pre-existing test that was not explicitly called out in Task 4 (see Completion Notes).

### Completion Notes List

- Added `showArchived` (default `false`, `useState`, no persistence) to `TaggingScaffoldManager`, wired to a new `Eye`/`EyeOff` icon toggle in the header row next to "Add Room" (AC #1, #6).
- Implemented the three-way per-node visibility computation at both Room→PowerPoint and PowerPoint→Device level: fully absent (archived, hidden, no visible children) / present-with-own-row-suppressed (archived, hidden, has visible children — renders as a plain `<div>` wrapper, no summary/badge/actions) / present-normally. Devices use the simple two-way filter. Satisfies AC #2, #3, #5.
- Confirmed `MoveDestinationList` and the create-Power-Point/create-Device pickers were left untouched — they already filter on `!archivedAt` off the unfiltered arrays, independent of `showArchived` (AC #4).
- Added the two new i18n keys (`hideArchivedToggle`, `showArchivedToggle`) to both `en-US` and `de-DE` catalogs.
- Test suite: added 4 new tests covering AC #1, #2/#3, #4, #5. Updated the 3 existing tests flagged in the story (archiving a Room/Power Point/Device, each now needs a toggle-on click before asserting the "Archived" badge). Also had to fix one additional pre-existing test — `still offers a valid destination when the Power Point's current Room has since been archived` — not explicitly enumerated in Task 4's three-test list: its archived Room has a live Power Point child, so under the new default its own row is suppressed and the test's `click(findByText('Kitchen'))` step no longer has anything to click; changed it to click the still-reachable `Counter outlet` Power Point directly.
- Full verification: `npx tsc -b` clean, `npx oxlint` clean (only 3 pre-existing unrelated warnings), `npx vitest run` — 147/147 tests passing across all 19 frontend test files (22/22 in the modified file). `dotnet build EnergyTracker.sln` succeeds (0 errors) — no backend files were touched by this story, consistent with Dev Notes' "zero backend surface".

### File List

- `web/src/components/tagging-scaffold/tagging-scaffold-manager.tsx` (modified)
- `web/src/components/tagging-scaffold/tagging-scaffold-manager.test.tsx` (modified)
- `web/src/locales/en-US/translation.json` (modified)
- `web/src/locales/de-DE/translation.json` (modified)

## Change Log

- 2026-08-23: Implemented the archived-item visibility toggle end to end (Tasks 1-5) — hide-by-default view filter with three-way cascade-safe rendering, new toggle control, i18n strings, and test coverage for AC #1-#6. Status set to "review".
- 2026-08-23: Adversarial code review (Blind Hunter, Edge Case Hunter, Acceptance Auditor, all against commit d2c79f0) found and fixed a real AC #5 violation: `roomHasVisibleChildren` only checked each direct Power Point's own `archivedAt`, never recursing into `powerPointHasVisibleChildren` — so a Room whose only Power Point was itself archived but had a live Device underneath was dropped from the tree entirely, silently hiding that Device. Fixed by making `roomHasVisibleChildren` recurse the same way the sibling render-time filter already did, with a regression test. Also fixed: story/sprint-status docs incorrectly claimed the epic file was left unedited by this story (it was, in the same commit); extracted a shared `ROOM_ROW_BORDER_CLASS` constant to de-duplicate a class string repeated across the suppressed-row and normal-row branches; added the Power-Point-level and Device-level round-trip toggle tests Task 4 called for but were missing. One item deferred (pre-existing, not introduced by this diff): no test exercises the German toggle strings specifically. Status set to "done".
