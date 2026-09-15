---
title: 'Flaky "hides the shared background-processing note" test under CI'
type: 'bugfix'
created: '2026-09-15'
status: 'done'
route: 'one-shot'
review_loop_iteration: 0
context: []
---

# Flaky "hides the shared background-processing note" test under CI

## Intent

**Problem:** `smart-plug-import-page.test.tsx`'s `'hides the shared background-processing note once the (only) queued item has completed'` failed CI's `App Deploy` run on `main` (`Build, test, and deploy` job, run `34930393603`, 2026-09-15) with `expect(element).not.toBeInTheDocument()` finding the "parsing this in the background" note still present. This is a pre-existing, previously-deferred flaky test (`deferred-work.md`, "Deferred from: code review of spec-3-10-cleanup-async-job (2026-09-12)") in a file untouched by the PR that triggered this run (#53, story 5.2) — it self-resolved on rerun back then and was not investigated further; it has now recurred and actually blocked `main`'s deploy pipeline.

**Root cause:** The shared "parsing this in the background" note's visibility (`SmartPlugImportPage`'s `activeIds.size > 0`) is not computed directly from each queue item's `job.state` in the same render. Instead, each `SmartPlugImportQueueItem` reports its active/inactive status up to the parent via a `useEffect` (`web/src/components/smart-plug-import/smart-plug-import-page.tsx:160-172`) that only fires *after* the item's own re-render (showing "Import complete") has already committed. So the item's "Import complete" text and the parent's note disappearing are two sequential React render passes, not one atomic update — under CI's slower/more contended scheduling, the second (effect-driven) pass doesn't always land inside the same tick as the first. The test asserted the note's absence with a synchronous `expect` immediately after a `waitFor` on "Import complete", giving the second render pass no room to land. Confirmed by code inspection (the effect-based cross-component signaling is a real, structural two-hop update, not a hypothesis) — not locally reproducible after 5+ repeated runs of just this test plus 5+ full-file runs, consistent with a CI-scheduling-dependent race, matching `deferred-work.md`'s own prior note.

**Approach:** No application bug — the note genuinely does disappear, just one render pass later than the test assumed. Widened the second assertion to its own `waitFor` (matching how the first assertion in the same test already tolerates async completion), so the test correctly waits for both sequential state updates instead of assuming they're simultaneous. No production code changed.

## Suggested Review Order

**Test fix**

- The synchronous `expect(...).not.toBeInTheDocument()` right after the "Import complete" `waitFor` is replaced with its own `waitFor`, tolerating the legitimate extra effect-driven render pass between the two DOM changes.
  [`smart-plug-import-page.test.tsx:263-271`](../../web/src/components/smart-plug-import/smart-plug-import-page.test.tsx#L263)

## Verification

- Ran the single test 8x and the full test file 5x locally: all green (not reproducible locally either way, consistent with the CI-scheduling-dependent nature already noted in `deferred-work.md`).
- Full frontend suite (293/293), `tsc -b`, `oxlint` all clean after the change.
