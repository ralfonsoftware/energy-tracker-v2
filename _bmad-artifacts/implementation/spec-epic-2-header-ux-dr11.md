---
title: 'Epic 2 header: add missing UX-DR11 citation'
type: 'chore'
created: '2026-08-22'
status: 'done'
route: 'one-shot'
---

# Epic 2 header: add missing UX-DR11 citation

## Intent

**Problem:** Epic 2's `UX-DRs:` rollup header line omitted `UX-DR11`, even though Story 2.5's own acceptance criteria (in the same file) already cite it directly — a documentation-consistency slip flagged by `implementation-readiness-report-2026-08-22.md`.

**Approach:** Add `UX-DR11 (liquid glass elevation)` to Epic 2's header line, in numeric order between `UX-DR9` and `UX-DR13`, so the rollup matches what the epic's own body already cites. No functional/code change.

## Suggested Review Order

- Header now lists UX-DR11 alongside the other UX-DRs Epic 2's stories actually cite.
  [`epic-2-meter-reading-pattern-detective-status-core.md:8`](../planning/epics/epic-2-meter-reading-pattern-detective-status-core.md#L8)

- The AC that already cited UX-DR11 before this fix — confirms the header now matches the body.
  [`epic-2-meter-reading-pattern-detective-status-core.md:196`](../planning/epics/epic-2-meter-reading-pattern-detective-status-core.md#L196)
