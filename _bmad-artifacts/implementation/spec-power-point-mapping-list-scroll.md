---
title: 'Scrollable Power Point list in the smart plug mapping dialog'
type: 'bugfix'
created: '2026-09-02'
status: 'done'
route: 'one-shot'
review_loop_iteration: 0
context: []
---

# Scrollable Power Point list in the smart plug mapping dialog

## Intent

**Problem:** In `PowerPointMappingDialog` (shown after a smart plug file import when a device tag needs mapping), the "map to an existing one" list of Power Points had no height cap or scroll behavior, so a household with many Power Points couldn't reach every row — the list simply overflowed the dialog with no way to scroll to the rest.

**Approach:** Cap the list container to `max-h-64` with `overflow-y-auto` (plus `overscroll-contain` to stop scroll-chaining into the page behind the modal), so every existing Power Point stays reachable regardless of count. Reviewed by Blind Hunter (adversarial review); the shared `DialogContent` primitive's own lack of a viewport-height constraint, and a minor scrollbar-induced layout-shift edge case, were confirmed pre-existing/out-of-scope and deferred to `deferred-work.md`.

## Suggested Review Order

**Scroll fix**

- Entry point: the list container gets a fixed max height, vertical scroll, and overscroll containment — this is the actual fix for the reported bug.
  [`power-point-mapping-dialog.tsx:184`](../../web/src/components/smart-plug-import/power-point-mapping-dialog.tsx#L184)

- Mockup-reference comment updated to note this diverges from the mock, which shows no scroll affordance.
  [`power-point-mapping-dialog.tsx:22`](../../web/src/components/smart-plug-import/power-point-mapping-dialog.tsx#L22)

**Test coverage**

- Verifies the list carries the scroll classes with many rows, and that an overflowing row is still clickable end-to-end (mapping fetch fires with the correct id).
  [`power-point-mapping-dialog.test.tsx:198`](../../web/src/components/smart-plug-import/power-point-mapping-dialog.test.tsx#L198)
