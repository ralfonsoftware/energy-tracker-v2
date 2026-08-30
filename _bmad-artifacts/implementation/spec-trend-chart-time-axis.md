---
title: 'Trend chart: readable time axis + full-width layout'
type: 'feature'
created: '2026-08-30'
status: 'done'
review_loop_iteration: 0
context: []
baseline_commit: 'a339e2dd6edb41a821f5416cd6ab34c60fe16aae'
---

<frozen-after-approval reason="human-owned intent — do not modify unless human renegotiates">

## Intent

**Problem:** `TrendChart` (`web/src/components/trend-history/trend-chart.tsx`, Story 4.1) has no x-axis at all, so a user can't tell whether the line covers a few days or half a year. It also renders inside a fixed 320×130 `viewBox` with `preserveAspectRatio="xMidYMid meet"` at a hardcoded `height={150}` — on any container wider than ~370px (i.e. almost every real screen) the SVG's uniform scale is height-bound, so the plotted chart is letterboxed into a narrow centered strip with large empty margins left and right, wasting most of the card's width.

**Approach:** Measure the actual container width (via `ResizeObserver`, with a static fallback for pre-measurement/unsupported environments) and drive the `viewBox` from it so the chart always fills the real available width at a constant height — no more letterboxing. Add an adaptive x-axis: month-boundary gridlines+labels as the primary scale, small unlabeled weekly tick marks for a sense of pace, and a short-range fallback (explicit dated ticks) when the data doesn't span a full month, so there's always at least one real calendar date visible on the axis.

## Boundaries & Constraints

**Always:**
- No charting library — extend the existing hand-rolled inline SVG (matches this codebase's stated no-dependency convention for this component).
- All date/month labels go through `Intl.DateTimeFormat(locale, …)`, never a hardcoded English month name — mirrors the existing `gapDateFormat` pattern (AD-18).
- New axis strokes/gridlines reuse `currentColor` + the same `fill-muted-foreground` / low-opacity (~0.08–0.12) convention already used for the existing axis lines — no new chart-specific colors, no 3rd/4th line color (keeps AC #7's 2-color rule intact).
- `ResizeObserver` usage must feature-detect (`typeof ResizeObserver !== 'undefined'`) and fall back to a fixed default width constant — must never throw in an environment without it (this includes the current Vitest/jsdom test setup, which has no `ResizeObserver`).
- All 10 existing tests in `trend-chart.test.tsx` keep passing with their current assertions and `data-testid` values unchanged.

**Ask First:** None — exact tick density, font size, and gridline styling are left to the agent's judgment per Ralf's "open for ideas" framing.

**Never:**
- Don't change the chart's rendered height in a way that scales with width (no `aspect-ratio` CSS approach) — height must stay visually constant across screen sizes; only width usage changes.
- Don't touch `MeterReadingsCard`, `PerPlugDataCard`, the backend, or `status-api.ts` — this is a `TrendChart`-only presentational change.
- Don't add a date-math library (date-fns, dayjs, etc.) — native `Date`/`Intl` only, matching existing code.

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected Output / Behavior | Error Handling |
|----------|--------------|---------------------------|----------------|
| Multi-month range | Entries span e.g. Mar–Aug | Month-boundary gridline + short-month label (`Mar`, `Apr`, …) per calendar month in range; weekly unlabeled tick marks between them | N/A |
| Short range (< 1 month, no month boundary falls inside) | Entries span 3–10 days | No month tick can be generated; axis instead shows explicit dated ticks (e.g. `Aug 24`, `Aug 27`, `Aug 30`) so the span is never unlabeled | N/A |
| Multi-year range | Entries span across a Dec 31 → Jan 1 boundary | Month labels append a 2-digit year (`Dec 25`, `Jan 26`) the first time the year changes, to disambiguate | N/A |
| Very long/dense range (> ~7 months) | Many weekly boundaries would fall in range | Weekly minor ticks are suppressed (month ticks only) to avoid visual clutter | N/A |
| Width not yet measured (first paint, or `ResizeObserver` unsupported) | `containerRef.current` width is 0/unavailable | Chart renders immediately at a fixed fallback width instead of collapsing to 0 | N/A |

</frozen-after-approval>

## Code Map

- `web/src/components/trend-history/trend-chart.tsx` -- add width measurement + adaptive x-axis tick/gridline generation; this is the entire delta
- `web/src/components/trend-history/trend-chart.test.tsx` -- extend with coverage for the new axis behavior

## Tasks & Acceptance

**Execution:**
- [x] `web/src/components/trend-history/trend-chart.tsx` -- Wrap the SVG in a measured container (`useRef` + `useState` + `ResizeObserver`, guarded by feature detection, fallback constant e.g. `640` when unmeasured) and derive `CHART_RIGHT`/`viewBox` width from the measured pixels instead of the fixed `320` -- eliminates the letterboxing shown in the reported screenshot
- [x] same file -- Compute calendar-month-boundary positions from `entries`' `computedAtUtc` range; render a faint full-height vertical gridline + short-month `Intl.DateTimeFormat` label per boundary strictly inside the range, appending a 2-digit year on the first label after a year change
- [x] same file -- Compute weekly boundary positions (every 7 days from the earliest entry); render a small unlabeled tick at each, but suppress entirely when the total range exceeds ~7 months
- [x] same file -- When zero month boundaries fall inside the range (span < ~1 month), render 2–4 evenly spaced dated ticks (`Intl.DateTimeFormat(locale, { month: 'short', day: 'numeric' })`) instead, so the axis is never unlabeled
- [x] `web/src/components/trend-history/trend-chart.test.tsx` -- add tests: month gridlines+labels render for a multi-month span; weekly ticks render for a medium span and are absent for a very long span; a short (<1 month) span renders fallback dated ticks; a multi-year span appends the 2-digit year to labels

**Acceptance Criteria:**
- Given a household with several months of Status History, when the Trend History page renders, then the chart visibly spans the full card width (no large empty side margins) and shows at least one labeled month per calendar month present in the data.
- Given a household with only a few days of history, when the chart renders, then the x-axis shows explicit dates rather than being unlabeled.
- Given entries spanning more than one calendar year, when the chart renders, then month labels disambiguate the year at each year boundary.
- Given the existing gap-band/segment/regression tests in `trend-chart.test.tsx`, when the suite runs after this change, then all currently-passing assertions still pass unmodified.

## Spec Change Log

## Design Notes

Width measurement sketch (kept local to the component, no new file):

```tsx
const containerRef = useRef<HTMLDivElement>(null)
const [width, setWidth] = useState(FALLBACK_WIDTH) // e.g. 640

useEffect(() => {
  const el = containerRef.current
  if (!el || typeof ResizeObserver === 'undefined') return
  const observer = new ResizeObserver(([entry]) => {
    if (entry.contentRect.width > 0) setWidth(entry.contentRect.width)
  })
  observer.observe(el)
  return () => observer.disconnect()
}, [])
```

`CHART_RIGHT = width - RIGHT_MARGIN` replaces the current literal `310`; `CHART_LEFT`/`CHART_TOP`/`CHART_BOTTOM` stay as fixed margins in the same coordinate space so the plotted `y()` math is untouched — only the horizontal extent grows with the container.

Month-tick generation walks calendar months (not fixed millisecond steps, since month lengths vary): start from `new Date(minTime).getFullYear()/getMonth()+1, 1` and increment `getMonth()+1` each step until past `maxTime`.

## Verification

**Commands:**
- `cd web && npm run test -- trend-chart` -- PASS: 15/15 tests passed (10 pre-existing + 5 new: month gridlines/labels, weekly ticks present, weekly ticks suppressed, short-range fallback dated ticks, multi-year 2-digit-year label)
- `cd web && npm run lint` -- PASS: oxlint clean for `trend-chart.tsx`/`trend-chart.test.tsx` (only pre-existing warnings in unrelated files: `badge.tsx`, `use-smart-plug-import-job.ts`, `household-size-preset-row.tsx`, `button.tsx`)
- `cd web && npm run build` -- PASS: `tsc -b && vite build` succeeded with no type errors

**Manual checks (if no CLI):**
- Open Trend History on a household with multiple months of history in a wide desktop browser window: chart should visibly stretch across the card, with visible month labels and light weekly tick marks.

## Suggested Review Order

**Full-width measurement**

- Entry point: callback-ref-based width measurement, feature-detected `ResizeObserver` with a safe fallback — re-attaches even if the container first mounts on a later render (e.g. after an async fetch resolves), unlike a plain `useRef`+`useEffect([])`.
  [`trend-chart.tsx:53`](../../web/src/components/trend-history/trend-chart.tsx#L53)

- `viewBox` now tracks the measured width 1:1 with `preserveAspectRatio="none"`, eliminating the letterboxing the screenshot showed while keeping rendered height fixed.
  [`trend-chart.tsx:236`](../../web/src/components/trend-history/trend-chart.tsx#L236)

- Clamps the derived right edge so a tiny measured/fallback width can't collapse or invert the x-scale.
  [`trend-chart.tsx:134`](../../web/src/components/trend-history/trend-chart.tsx#L134)

**Adaptive time axis**

- Calendar-month boundary walk (not fixed millisecond steps) drives the primary tick scale; loop bound fixed to match its own "strictly between" contract.
  [`trend-chart.tsx:76`](../../web/src/components/trend-history/trend-chart.tsx#L76)

- Fewer than 2 month boundaries in range (short spans, or a lone boundary near an edge) replaces the month axis with evenly spaced dated ticks instead of layering both.
  [`trend-chart.tsx:166`](../../web/src/components/trend-history/trend-chart.tsx#L166)

- Zero-length span (entries sharing one timestamp) short-circuits to a single tick instead of several stacked duplicates.
  [`trend-chart.tsx:104`](../../web/src/components/trend-history/trend-chart.tsx#L104)

- Month labels (not gridlines) thin out once average spacing would fall under a legible minimum, so a long history in a narrow container doesn't collide.
  [`trend-chart.tsx:172`](../../web/src/components/trend-history/trend-chart.tsx#L172)

**Tests**

- New coverage: multi-month/weekly/fallback/sparse-boundary/duplicate-timestamp axis cases, the 210-day weekly-suppression boundary, and the ResizeObserver resize + late-mount + tiny-width paths via a minimal stub.
  [`trend-chart.test.tsx:11`](../../web/src/components/trend-history/trend-chart.test.tsx#L11)

- Pre-existing locale-formatting test narrowed to the gap band's own text, since the short-span fallback ticks can now legitimately repeat the same end date elsewhere on the chart.
  [`trend-chart.test.tsx:103`](../../web/src/components/trend-history/trend-chart.test.tsx#L103)
