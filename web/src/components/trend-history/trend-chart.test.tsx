import { render, screen } from '@testing-library/react'
import { describe, expect, it } from 'vitest'
import { TrendChart } from './trend-chart'
import type { StatusHistoryEntryDto } from '@/lib/status-api'

function entry(overrides: Partial<StatusHistoryEntryDto> = {}): StatusHistoryEntryDto {
  return {
    status: 'withinRange',
    paceToDateKwh: 1000,
    baselineToDateKwh: 1000,
    isLowConfidence: false,
    computedAtUtc: '2026-08-01T00:00:00+00:00',
    gapBeforeThisEntry: false,
    ...overrides,
  }
}

describe('TrendChart', () => {
  it('renders the empty state with 0 entries', () => {
    render(<TrendChart entries={[]} locale="en-US" />)

    expect(screen.getByText('Not enough history yet to show a trend.')).toBeInTheDocument()
  })

  it('renders the empty state with 1 entry', () => {
    render(<TrendChart entries={[entry()]} locale="en-US" />)

    expect(screen.getByText('Not enough history yet to show a trend.')).toBeInTheDocument()
  })

  it('renders a path segment for a contiguous run of entries', () => {
    const entries = [
      entry({ computedAtUtc: '2026-08-01T00:00:00+00:00', paceToDateKwh: 1000, baselineToDateKwh: 1000 }),
      entry({ computedAtUtc: '2026-08-02T00:00:00+00:00', paceToDateKwh: 1100, baselineToDateKwh: 1000 }),
    ]

    const { container } = render(<TrendChart entries={entries} locale="en-US" />)

    expect(container.querySelectorAll('[data-testid="trend-chart-segment"]')).toHaveLength(1)
    expect(container.querySelectorAll('[data-testid="trend-chart-gap-band"]')).toHaveLength(0)
  })

  it('renders a visible gap with no connecting path segment where gapBeforeThisEntry is true', () => {
    const entries = [
      entry({ computedAtUtc: '2026-08-01T00:00:00+00:00' }),
      entry({ computedAtUtc: '2026-09-01T00:00:00+00:00', gapBeforeThisEntry: true }),
      entry({ computedAtUtc: '2026-09-02T00:00:00+00:00' }),
    ]

    const { container } = render(<TrendChart entries={entries} locale="en-US" />)

    // Only the second->third pair connects; the first->second pair is a gap, not a segment.
    expect(container.querySelectorAll('[data-testid="trend-chart-segment"]')).toHaveLength(1)
    expect(container.querySelectorAll('[data-testid="trend-chart-gap-band"]')).toHaveLength(1)
  })

  it('uses the trending color for a Trending-status segment', () => {
    const entries = [entry({ status: 'withinRange' }), entry({ status: 'trending', computedAtUtc: '2026-08-02T00:00:00+00:00' })]

    const { container } = render(<TrendChart entries={entries} locale="en-US" />)

    const segment = container.querySelector('[data-testid="trend-chart-segment"]')
    expect(segment).toHaveAttribute('stroke', 'var(--color-status-trending)')
  })

  it('uses the within-range color for WithinRange and BelowBaseline segments', () => {
    const entries = [
      entry({ status: 'trending', computedAtUtc: '2026-08-01T00:00:00+00:00' }),
      entry({ status: 'withinRange', computedAtUtc: '2026-08-02T00:00:00+00:00' }),
      entry({ status: 'belowBaseline', computedAtUtc: '2026-08-03T00:00:00+00:00' }),
    ]

    const { container } = render(<TrendChart entries={entries} locale="en-US" />)

    const segments = container.querySelectorAll('[data-testid="trend-chart-segment"]')
    expect(segments).toHaveLength(2)
    expect(segments[0]).toHaveAttribute('stroke', 'var(--color-status-within-range)')
    expect(segments[1]).toHaveAttribute('stroke', 'var(--color-status-within-range)')
  })

  it('formats the caption and gap label using the given locale, not the environment default (AD-18)', () => {
    const entries = [
      entry({ computedAtUtc: '2026-08-01T00:00:00+00:00', paceToDateKwh: 1000, baselineToDateKwh: 1000 }),
      entry({
        computedAtUtc: '2026-09-15T00:00:00+00:00',
        gapBeforeThisEntry: true,
        paceToDateKwh: 1500,
        baselineToDateKwh: 1300,
      }),
    ]

    render(<TrendChart entries={entries} locale="de-DE" />)

    // de-DE month/day formatting ("15. Sept.") differs from en-US ("Sep 15") — proves the locale
    // prop actually reaches Intl.DateTimeFormat rather than being ignored.
    expect(screen.getByText(/Sept\./)).toBeInTheDocument()
  })

  it('plots an over-baseline point above the zero line, matching the "over" caption (regression)', () => {
    // Bug: the point was plotted below the zero line (toward the −axisMax label) while the
    // caption said "over the baseline" — pace exceeding baseline must render above zero (toward
    // +axisMax), matching computeStatusDifference's sign (rawDifference = pace − baseline > 0).
    const entries = [
      entry({ computedAtUtc: '2026-08-01T00:00:00+00:00', paceToDateKwh: 1000, baselineToDateKwh: 1000 }),
      entry({ computedAtUtc: '2026-08-02T00:00:00+00:00', paceToDateKwh: 1106, baselineToDateKwh: 1000 }),
    ]

    const { container } = render(<TrendChart entries={entries} locale="en-US" />)

    expect(screen.getByText('Currently 106 kWh over baseline.')).toBeInTheDocument()

    const point = container.querySelector('[data-testid="trend-chart-point"]')
    const zeroLine = container.querySelector('line[stroke-dasharray]')
    const pointY = Number(point?.getAttribute('cy'))
    const baselineY = Number(zeroLine?.getAttribute('y1'))
    expect(pointY).toBeLessThan(baselineY)
  })

  it('plots an under-baseline point below the zero line, matching the "under" caption (regression)', () => {
    const entries = [
      entry({ computedAtUtc: '2026-08-01T00:00:00+00:00', paceToDateKwh: 1000, baselineToDateKwh: 1000 }),
      entry({ computedAtUtc: '2026-08-02T00:00:00+00:00', paceToDateKwh: 894, baselineToDateKwh: 1000 }),
    ]

    const { container } = render(<TrendChart entries={entries} locale="en-US" />)

    expect(screen.getByText('Currently 106 kWh under baseline.')).toBeInTheDocument()

    const point = container.querySelector('[data-testid="trend-chart-point"]')
    const zeroLine = container.querySelector('line[stroke-dasharray]')
    const pointY = Number(point?.getAttribute('cy'))
    const baselineY = Number(zeroLine?.getAttribute('y1'))
    expect(pointY).toBeGreaterThan(baselineY)
  })

  it('renders distinct, stable React keys per segment/gap even when two entries share a ComputedAtUtc value', () => {
    // StatusSnapshotRepository's own ordering comment documents ComputedAtUtc is not guaranteed
    // unique — two rows can tie and are only disambiguated by Id (never sent to the client), so
    // a key built from the timestamp alone would collide here.
    const sharedTimestamp = '2026-08-01T00:00:00+00:00'
    const entries = [
      entry({ computedAtUtc: sharedTimestamp }),
      entry({ computedAtUtc: sharedTimestamp }),
      entry({ computedAtUtc: sharedTimestamp }),
    ]

    const { container } = render(<TrendChart entries={entries} locale="en-US" />)

    expect(container.querySelectorAll('[data-testid="trend-chart-segment"]')).toHaveLength(2)
  })
})
