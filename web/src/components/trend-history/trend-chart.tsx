import { useCallback, useEffect, useState } from 'react'
import { useTranslation } from 'react-i18next'
import { computeStatusDifference } from '@/lib/status-difference'
import type { StatusHistoryEntryDto } from '@/lib/status-api'

interface TrendChartProps {
  entries: StatusHistoryEntryDto[]
  // AD-18: the household's own Locale, same as every other timestamp/number display in this
  // codebase — without this, axis numbers and gap-band date ranges fell back to the browser's
  // environment locale instead of matching the rest of the page (MeterReadingsCard included).
  locale: string
}

const CHART_LEFT = 34
const CHART_TOP = 10
const CHART_BOTTOM = 106
const BASELINE_Y = (CHART_TOP + CHART_BOTTOM) / 2

// Reserved space on the right edge so the last month/date label doesn't clip against the
// viewBox boundary — mirrors CHART_LEFT's role of reserving room for the y-axis labels.
const RIGHT_MARGIN = 10

// The SVG's viewBox width now tracks the real measured container width 1:1 (see
// useMeasuredWidth below) so the chart always fills the card with no letterboxing. Before that
// measurement lands (first paint, or in an environment without ResizeObserver, e.g. Vitest/jsdom)
// it renders at this fixed fallback instead of collapsing to 0 width.
const FALLBACK_WIDTH = 640

// Rendered (CSS pixel) height stays fixed regardless of width so the chart's visual height never
// scales with the container — only the horizontal extent grows. preserveAspectRatio="none" makes
// this an explicit, independent vertical scale factor instead of relying on "meet" to pick it.
const RENDER_HEIGHT = 150
const VIEWBOX_HEIGHT = 130

// Below this average pixel spacing between month ticks, labels start colliding — thin them out
// rather than let short-month-name text overlap on narrow/mobile widths.
const MIN_MONTH_LABEL_SPACING_PX = 28

const WEEK_MS = 7 * 24 * 60 * 60 * 1000
// "~7 months" using 30-day months, per the spec's own fuzzy wording — beyond this, one tick per
// week would be too dense to read and is suppressed entirely in favor of month ticks alone.
const WEEKLY_TICK_SUPPRESSION_MS = 210 * 24 * 60 * 60 * 1000

// Measures the actual rendered width of the returned callback ref's element via ResizeObserver,
// feature-detected so this never throws in an environment without it (notably the current
// Vitest/jsdom setup) — it just keeps returning `fallback` forever in that case.
//
// Uses a callback ref (state, not useRef) so the observe-effect re-runs whenever the underlying
// DOM node changes — including the case where this component first renders its empty state (no
// container in the tree yet) and only mounts the real container on a later re-render once entries
// arrive. A plain `useRef` + `useEffect(fn, [])` would attach only once at first mount and never
// notice a node that didn't exist yet at that point.
function useMeasuredWidth(fallback: number): [(node: HTMLDivElement | null) => void, number] {
  const [node, setNode] = useState<HTMLDivElement | null>(null)
  const [width, setWidth] = useState(fallback)
  const ref = useCallback((el: HTMLDivElement | null) => setNode(el), [])

  useEffect(() => {
    if (!node || typeof ResizeObserver === 'undefined') return

    const observer = new ResizeObserver((observedEntries) => {
      const observedWidth = observedEntries[0]?.contentRect.width
      if (observedWidth && observedWidth > 0) setWidth(observedWidth)
    })
    observer.observe(node)
    return () => observer.disconnect()
  }, [node])

  return [ref, width]
}

// Calendar-month boundaries (the 1st of each month) strictly between minTime and maxTime, in
// chronological order. Walks calendar months rather than fixed millisecond steps since month
// lengths vary (Design Notes) — starts at the 1st of the month *after* minTime's own month, since
// minTime's month is already represented by the data starting partway through it.
function getMonthBoundaries(minTime: number, maxTime: number): Date[] {
  const boundaries: Date[] = []
  const start = new Date(minTime)
  let cursor = new Date(start.getFullYear(), start.getMonth() + 1, 1)

  while (cursor.getTime() < maxTime) {
    boundaries.push(cursor)
    cursor = new Date(cursor.getFullYear(), cursor.getMonth() + 1, 1)
  }

  return boundaries
}

// Weekly boundaries every 7 days from the earliest entry, excluding the endpoints themselves.
function getWeekBoundaries(minTime: number, maxTime: number): number[] {
  const boundaries: number[] = []
  for (let t = minTime + WEEK_MS; t < maxTime; t += WEEK_MS) {
    boundaries.push(t)
  }
  return boundaries
}

// Fallback for a span with fewer than 2 calendar-month boundaries inside it: 2-4 evenly spaced
// points (including both ends) so the axis always shows at least one real calendar date. Entries
// can share an identical ComputedAtUtc (StatusSnapshotRepository's own ordering comment documents
// this isn't guaranteed unique) — a zero-length span would otherwise produce several ticks that
// all collapse onto the same instant, so that case short-circuits to a single tick.
function getFallbackDatedTicks(minTime: number, maxTime: number): number[] {
  if (maxTime <= minTime) return [minTime]

  const spanDays = (maxTime - minTime) / (24 * 60 * 60 * 1000)
  const count = spanDays <= 1 ? 2 : spanDays <= 4 ? 3 : 4

  const ticks: number[] = []
  for (let i = 0; i < count; i++) {
    const fraction = i / (count - 1)
    ticks.push(minTime + fraction * (maxTime - minTime))
  }
  return ticks
}

// No charting library (this codebase has none) — hand-rolled inline SVG mirroring
// mockups/key-trend-history.html's structure. Colors reuse the existing --color-status-* CSS
// custom properties (index.css) — exactly 2 line colors, never a 3rd/4th chart-specific color
// (AC #7): Trending gets its own color, both WithinRange and BelowBaseline share the
// within-range color.
export function TrendChart({ entries, locale }: TrendChartProps) {
  const { t } = useTranslation()
  const [containerRef, width] = useMeasuredWidth(FALLBACK_WIDTH)
  const numberFormat = new Intl.NumberFormat(locale, { maximumFractionDigits: 0 })
  const gapDateFormat = new Intl.DateTimeFormat(locale, { month: 'short', day: 'numeric' })

  if (entries.length < 2) {
    return <p className="text-muted-foreground text-sm">{t('trendHistory.emptyState')}</p>
  }

  // Clamped so an unexpectedly tiny measured/fallback width can't push CHART_RIGHT at or below
  // CHART_LEFT, which would collapse or invert the x-axis scale.
  const CHART_RIGHT = Math.max(CHART_LEFT + 1, width - RIGHT_MARGIN)

  // rawDifference = pace − baseline: positive means over baseline, negative means under (same
  // sign the caption below uses). Plot it as-is so "over" renders above the zero line (toward
  // +axisMax) and "under" renders below it (toward −axisMax) — matching the caption's wording.
  const values = entries.map((entry) => computeStatusDifference(entry.paceToDateKwh, entry.baselineToDateKwh).rawDifference)
  const maxAbsValue = Math.max(1, ...values.map((v) => Math.abs(v)))
  const halfHeight = (CHART_BOTTOM - CHART_TOP) / 2

  const times = entries.map((entry) => new Date(entry.computedAtUtc).getTime())
  const minTime = times[0]
  const maxTime = times[times.length - 1]
  const timeSpan = maxTime - minTime

  const xAtTime = (time: number) => (timeSpan > 0 ? CHART_LEFT + ((time - minTime) / timeSpan) * (CHART_RIGHT - CHART_LEFT) : (CHART_LEFT + CHART_RIGHT) / 2)
  const x = (index: number) => xAtTime(times[index])
  const y = (index: number) => BASELINE_Y - (values[index] / maxAbsValue) * halfHeight

  // Never draws the maxAbsValue-derived tick label as a rounded literal '40' — it's whatever this
  // series' own extent happens to be, unlike the mockup's fixed illustrative example.
  const axisMax = Math.round(maxAbsValue)

  // Adaptive x-axis: month-boundary ticks as the primary scale, with a short-range fallback of
  // explicit dated ticks when no month boundary falls inside the range, plus unlabeled weekly
  // ticks for a sense of pace (suppressed once the range gets long enough to make them clutter).
  const monthBoundaries = getMonthBoundaries(minTime, maxTime)
  const weekBoundaries = timeSpan <= WEEKLY_TICK_SUPPRESSION_MS ? getWeekBoundaries(minTime, maxTime) : []
  // Fewer than 2 month boundaries isn't enough to convey the span on its own (a single boundary
  // reads as "somewhere in this month" with no sense of how much history surrounds it) — replace
  // the month-tick axis with explicit dated ticks in that case too, not just the zero-boundary
  // case. Replacing rather than layering both avoids two differently-formatted ticks landing next
  // to each other near the same date.
  const monthTicksVisible = monthBoundaries.length >= 2
  const fallbackDatedTicks = monthTicksVisible ? [] : getFallbackDatedTicks(minTime, maxTime)

  // A long history rendered in a narrow container can pack many month boundaries into little
  // pixel space — thin the *labels* (not the gridlines, which stay one per boundary) by skipping
  // every Nth one once the average spacing would fall under a legible minimum.
  const monthLabelStride =
    monthBoundaries.length > 0
      ? Math.max(1, Math.ceil((monthBoundaries.length * MIN_MONTH_LABEL_SPACING_PX) / (CHART_RIGHT - CHART_LEFT)))
      : 1

  const monthShortFormat = new Intl.DateTimeFormat(locale, { month: 'short' })
  const yearShortFormat = new Intl.DateTimeFormat(locale, { year: '2-digit' })
  const datedTickFormat = new Intl.DateTimeFormat(locale, { month: 'short', day: 'numeric' })

  let lastLabeledYear = new Date(minTime).getFullYear()
  const monthTicks = monthBoundaries.map((boundary) => {
    const year = boundary.getFullYear()
    let label = monthShortFormat.format(boundary)
    if (year !== lastLabeledYear) {
      // First label after a year change disambiguates it with a 2-digit year — subsequent labels
      // within the same year don't repeat it.
      label = `${label} ${yearShortFormat.format(boundary)}`
      lastLabeledYear = year
    }
    return { time: boundary.getTime(), label }
  })

  const segments: { key: string; d: string; color: string; status: string }[] = []
  const gapBands: { key: string; x1: number; x2: number; label: string }[] = []

  for (let i = 1; i < entries.length; i++) {
    if (entries[i].gapBeforeThisEntry) {
      gapBands.push({
        // Index-qualified — StatusSnapshotRepository's own ordering comment documents that
        // ComputedAtUtc is not guaranteed unique across rows (Id is the tiebreak), so the
        // timestamp alone can't be trusted as a React key.
        key: `gap-${i}-${entries[i].computedAtUtc}`,
        x1: x(i - 1),
        x2: x(i),
        label: t('trendHistory.gapLabel', {
          range: `${gapDateFormat.format(new Date(entries[i - 1].computedAtUtc))}–${gapDateFormat.format(new Date(entries[i].computedAtUtc))}`,
        }),
      })
      continue
    }

    // Colored by the LATER point's Status (AC #7).
    const color =
      entries[i].status === 'trending' ? 'var(--color-status-trending)' : 'var(--color-status-within-range)'

    segments.push({
      key: `segment-${i}-${entries[i].computedAtUtc}`,
      d: `M${x(i - 1)},${y(i - 1)} L${x(i)},${y(i)}`,
      color,
      status: entries[i].status,
    })
  }

  const lastIndex = entries.length - 1
  const latestDiff = computeStatusDifference(entries[lastIndex].paceToDateKwh, entries[lastIndex].baselineToDateKwh)
  // Mirrors dashboard.status.body.onPace/underPace/overPace's three-full-sentence convention
  // (Story 2.5) rather than a single template with a {{direction}} slot — "under"/"over" can't be
  // interpolated as a bare word across locales without losing agreement/case.
  const caption =
    latestDiff.sign === 'on'
      ? t('trendHistory.chartCaption.on')
      : t(`trendHistory.chartCaption.${latestDiff.sign}`, { kwh: numberFormat.format(latestDiff.roundedMagnitude) })

  return (
    <div ref={containerRef}>
      <svg
        viewBox={`0 0 ${width} ${VIEWBOX_HEIGHT}`}
        width="100%"
        height={RENDER_HEIGHT}
        preserveAspectRatio="none"
        role="img"
        aria-label={caption}
      >
        <line x1={CHART_LEFT} y1={CHART_TOP} x2={CHART_LEFT} y2={CHART_BOTTOM} stroke="currentColor" strokeOpacity={0.12} />
        <line x1={CHART_LEFT} y1={CHART_BOTTOM} x2={CHART_RIGHT} y2={CHART_BOTTOM} stroke="currentColor" strokeOpacity={0.12} />
        <line
          x1={CHART_LEFT}
          y1={BASELINE_Y}
          x2={CHART_RIGHT}
          y2={BASELINE_Y}
          stroke="currentColor"
          strokeOpacity={0.3}
          strokeDasharray="3 3"
        />
        <text x={0} y={BASELINE_Y + 3} className="fill-muted-foreground text-[8.5px]">0</text>
        <text x={0} y={CHART_TOP + 4} className="fill-muted-foreground text-[8.5px]">
          +{axisMax}
        </text>
        <text x={0} y={CHART_BOTTOM + 3} className="fill-muted-foreground text-[8.5px]">
          −{axisMax}
        </text>

        {(monthTicksVisible ? monthTicks : []).map(({ time, label }, index) => {
          const tickX = xAtTime(time)
          return (
            <g key={`month-${time}`} data-testid="trend-chart-month-tick">
              <line x1={tickX} y1={CHART_TOP} x2={tickX} y2={CHART_BOTTOM} stroke="currentColor" strokeOpacity={0.08} />
              {index % monthLabelStride === 0 && (
                <text x={tickX} y={CHART_BOTTOM + 12} textAnchor="middle" className="fill-muted-foreground text-[7.5px]">
                  {label}
                </text>
              )}
            </g>
          )
        })}

        {weekBoundaries.map((time) => {
          const tickX = xAtTime(time)
          return (
            <line
              key={`week-${time}`}
              data-testid="trend-chart-week-tick"
              x1={tickX}
              y1={CHART_BOTTOM}
              x2={tickX}
              y2={CHART_BOTTOM + 4}
              stroke="currentColor"
              strokeOpacity={0.2}
            />
          )
        })}

        {fallbackDatedTicks.map((time, index) => {
          const tickX = xAtTime(time)
          return (
            <g key={`dated-${index}-${time}`} data-testid="trend-chart-dated-tick">
              <line x1={tickX} y1={CHART_BOTTOM} x2={tickX} y2={CHART_BOTTOM + 4} stroke="currentColor" strokeOpacity={0.2} />
              <text x={tickX} y={CHART_BOTTOM + 12} textAnchor="middle" className="fill-muted-foreground text-[7.5px]">
                {datedTickFormat.format(new Date(time))}
              </text>
            </g>
          )
        })}

        {gapBands.map((band) => (
          <g key={band.key}>
            <rect
              x={band.x1}
              y={CHART_TOP}
              width={Math.max(0, band.x2 - band.x1)}
              height={CHART_BOTTOM - CHART_TOP}
              fill="var(--color-status-trending)"
              fillOpacity={0.08}
              data-testid="trend-chart-gap-band"
            />
            <text x={band.x1 + 2} y={CHART_TOP + 12} className="fill-muted-foreground text-[8px] font-semibold">
              {band.label}
            </text>
          </g>
        ))}

        {segments.map((segment) => (
          <path
            key={segment.key}
            d={segment.d}
            fill="none"
            stroke={segment.color}
            strokeWidth={2.4}
            strokeLinecap="round"
            strokeLinejoin="round"
            data-testid="trend-chart-segment"
            data-status={segment.status}
          />
        ))}

        <circle
          cx={x(lastIndex)}
          cy={y(lastIndex)}
          r={3}
          fill={entries[lastIndex].status === 'trending' ? 'var(--color-status-trending)' : 'var(--color-status-within-range)'}
          data-testid="trend-chart-point"
        />
      </svg>
      <p className="text-muted-foreground mt-2 text-sm">{caption}</p>
    </div>
  )
}
