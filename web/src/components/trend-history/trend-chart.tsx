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
const CHART_RIGHT = 310
const CHART_TOP = 10
const CHART_BOTTOM = 106
const BASELINE_Y = (CHART_TOP + CHART_BOTTOM) / 2

// No charting library (this codebase has none) — hand-rolled inline SVG mirroring
// mockups/key-trend-history.html's structure. Colors reuse the existing --color-status-* CSS
// custom properties (index.css) — exactly 2 line colors, never a 3rd/4th chart-specific color
// (AC #7): Trending gets its own color, both WithinRange and BelowBaseline share the
// within-range color.
export function TrendChart({ entries, locale }: TrendChartProps) {
  const { t } = useTranslation()
  const numberFormat = new Intl.NumberFormat(locale, { maximumFractionDigits: 0 })
  const gapDateFormat = new Intl.DateTimeFormat(locale, { month: 'short', day: 'numeric' })

  if (entries.length < 2) {
    return <p className="text-muted-foreground text-sm">{t('trendHistory.emptyState')}</p>
  }

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

  const x = (index: number) => (timeSpan > 0 ? CHART_LEFT + ((times[index] - minTime) / timeSpan) * (CHART_RIGHT - CHART_LEFT) : (CHART_LEFT + CHART_RIGHT) / 2)
  const y = (index: number) => BASELINE_Y - (values[index] / maxAbsValue) * halfHeight

  // Never draws the maxAbsValue-derived tick label as a rounded literal '40' — it's whatever this
  // series' own extent happens to be, unlike the mockup's fixed illustrative example.
  const axisMax = Math.round(maxAbsValue)

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
    <div>
      <svg viewBox="0 0 320 130" width="100%" height="150" preserveAspectRatio="xMidYMid meet" role="img" aria-label={caption}>
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
