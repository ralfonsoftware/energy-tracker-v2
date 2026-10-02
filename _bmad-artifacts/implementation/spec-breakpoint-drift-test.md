---
title: 'Breakpoint and column single source of truth: drift test (Epic 8 retro action item #3)'
type: 'chore'
created: '2026-10-02'
status: 'done'
baseline_commit: 'bb9aedf8024410e44c0fccbb5a4b1b8a9324c292'
review_loop_iteration: 0
context:
  - '{project-root}/_bmad-artifacts/implementation/epic-8-retro-2026-10-02.md'
  - '{project-root}/_bmad-artifacts/project-context.md'
---

<frozen-after-approval reason="human-owned intent — do not modify unless human renegotiates">

## Intent

**Problem:** The 660px wide breakpoint exists as `--breakpoint-wide` (CSS), `WIDE_QUERY` (hook), matchMedia stubs and assertions in unit tests, and boundary widths in three Playwright specs. The 900px column is a `wide:max-w-[900px]` literal on four pages plus e2e assertions. Nothing ties these copies together, so changing one silently leaves the others stale (flagged in 8.12 and the Epic 8 retro).

**Approach:** One Vitest drift test reads the copies off disk, treats `--breakpoint-wide` in `index.css` as the breakpoint's single source, and fails naming the stale copy if any other drifts. The column width has no CSS source, so the test requires all copies to agree with each other. No production code changes.

## Boundaries & Constraints

**Always:** Read files via Node `fs` like `src/color-tokens.contrast.test.ts` (`/// <reference types="node" />`, `import.meta.dirname`), never `?raw`. Derive N (breakpoint) and C (column) from the files; hard-code neither in the test. Failure messages name the file and the found vs expected value. Scan e2e files with `//` comments stripped, so prose like "659px" cannot cause a false failure.

**Ask First:** Any production-code change, including exporting `WIDE_QUERY` or adding a shared constants module. Any new dependency.

**Never:** Visual-regression tooling. Changing the breakpoint or column values. Touching the tab-order or locale-sweep specs beyond reading them.

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected Output / Behavior | Error Handling |
|----------|--------------|---------------------------|----------------|
| Breakpoint copies | `--breakpoint-wide: Npx`; `WIDE_QUERY` in the hook; every `min-width: Xpx` in `src/**` (non-test and test, stubs included) | Every X equals N | Fail naming file and X |
| e2e boundary widths | Every literal 600-699 in `e2e/*.spec.ts` code | Each is N-1 or N; both N-1 and N appear in `app-shell.spec.ts` | Fail naming file and stale literal |
| Column copies | Every `wide:max-w-[Cpx]` in non-test `src/**`; `toBeCloseTo(X` in `e2e/app-shell.spec.ts` | At least 4 page wrappers found; all agree on one C; every e2e X equals C | Fail listing the files with differing values |
| Scan sanity | Regexes find zero matches (e.g. after a rename) | Fail, not pass vacuously | "no copies found" naming the pattern |

</frozen-after-approval>

## Code Map

- `web/src/index.css` -- `--breakpoint-wide: 660px`, the breakpoint source.
- `web/src/hooks/use-wide-breakpoint.ts` -- `WIDE_QUERY` (module-private).
- `web/src/test/wide-viewport.ts` -- matchMedia stub with its own query literal.
- `web/src/components/{dashboard,trend-history,tariff,settings}/*-page.tsx` -- the four `wide:max-w-[900px]` wrappers.
- `web/e2e/{app-shell,locale-theme-sweep,tab-order}.spec.ts` -- boundary widths (659/660) and `toBeCloseTo(900` column checks.
- `web/src/color-tokens.contrast.test.ts` -- idiom to copy for reading files from disk.

## Tasks & Acceptance

**Execution:**
- [x] `web/src/layout-constants.drift.test.ts` -- new Vitest file implementing the matrix: breakpoint tests, e2e boundary tests, column tests, and non-vacuity guards -- closes the single-source gap with no production change
- [x] `_bmad-artifacts/project-context.md` -- add one line under Frontend rules: the breakpoint and column width are guarded by `layout-constants.drift.test.ts`; change `--breakpoint-wide` first and let the test list the other copies -- prevents recurrence
- [x] `_bmad-artifacts/implementation/deferred-work.md` -- append at END a `[resolved: spec-breakpoint-drift-test]` note for the 8.12 "660px lives in three places with no sync check" item and the `max-w-[900px]` repetition -- append-only convention

**Acceptance Criteria:**
- Given the current repo, when `npm test` runs in `web`, then the new test passes.
- Given `--breakpoint-wide` changed to 700px (local mutation, reverted afterwards), when the test runs, then it fails naming the hook, stubs, and e2e literals.
- Given one `wide:max-w-[900px]` changed to 880px (local mutation, reverted afterwards), when the test runs, then it fails naming that file.

## Verification

**Commands:**
- `cd web && npx vitest run src/layout-constants.drift.test.ts` -- expected: all pass
- `cd web && npm test && npx tsc -b && npm run lint` -- expected: green

## Suggested Review Order

**Breakpoint copies**

- Source of truth is read from index.css; a missing token fails loudly at load.
  [`layout-constants.drift.test.ts:43`](../../web/src/layout-constants.drift.test.ts#L43)

- Every `min-width: Npx` in src, hook and stub required, must equal the source.
  [`layout-constants.drift.test.ts:44`](../../web/src/layout-constants.drift.test.ts#L44)

- e2e 6xx literals must be N-1 or N; app-shell must pin both sides.
  [`layout-constants.drift.test.ts:56`](../../web/src/layout-constants.drift.test.ts#L56)

**Column width**

- Page wrappers agree on a majority value, so the odd page is blamed.
  [`layout-constants.drift.test.ts:79`](../../web/src/layout-constants.drift.test.ts#L79)

- e2e column assertions must match the wrappers.
  [`layout-constants.drift.test.ts:86`](../../web/src/layout-constants.drift.test.ts#L86)

**Docs**

- Guard documented in Frontend rules, with its scanning limits.
  [`project-context.md:75`](../project-context.md#L75)

- Resolved note plus open follow-up for a shared constant.
  [`deferred-work.md:553`](deferred-work.md#L553)
