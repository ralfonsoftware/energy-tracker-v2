import { act, render, screen } from '@testing-library/react'
import { afterEach, describe, expect, it } from 'vitest'
import { TrendChart } from './trend-chart'
import type { StatusHistoryEntryDto } from '@/lib/status-api'

const DAY_MS = 24 * 60 * 60 * 1000

// Minimal ResizeObserver stub — jsdom has no real implementation. `trigger` lets a test simulate
// the browser reporting a measured width, exercising the actual resize path instead of only the
// FALLBACK_WIDTH branch every other test in this file runs through by default.
class ResizeObserverStub {
  static instances: ResizeObserverStub[] = []
  private readonly callback: ResizeObserverCallback

  constructor(callback: ResizeObserverCallback) {
    this.callback = callback
    ResizeObserverStub.instances.push(this)
  }

  observe() {}
  disconnect() {}

  trigger(width: number) {
    this.callback([{ contentRect: { width } } as ResizeObserverEntry], this as unknown as ResizeObserver)
  }
}

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
    // prop actually reaches Intl.DateTimeFormat rather than being ignored. Matches the gap band's
    // own range label specifically (rather than a bare /Sept\./) since the short (< 1 month, no
    // in-range boundary) Aug 1 -> Sep 15 span here also renders locale-formatted fallback dated
    // ticks that can legitimately repeat the same end date.
    expect(screen.getByText(/1\. Aug\.–15\. Sept\./)).toBeInTheDocument()
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

  it('renders a month gridline + short-month label per calendar month for a multi-month span', () => {
    const entries = [
      entry({ computedAtUtc: '2026-03-05T12:00:00+00:00' }),
      entry({ computedAtUtc: '2026-08-20T12:00:00+00:00' }),
    ]

    const { container } = render(<TrendChart entries={entries} locale="en-US" />)

    // Boundaries strictly inside Mar 5 - Aug 20: Apr 1, May 1, Jun 1, Jul 1, Aug 1.
    const monthTicks = container.querySelectorAll('[data-testid="trend-chart-month-tick"]')
    expect(monthTicks).toHaveLength(5)
    expect(container.textContent).toContain('Apr')
    expect(container.textContent).toContain('Aug')
  })

  it('renders weekly tick marks for a medium span', () => {
    const entries = [
      entry({ computedAtUtc: '2026-06-01T12:00:00+00:00' }),
      entry({ computedAtUtc: '2026-07-30T12:00:00+00:00' }),
    ]

    const { container } = render(<TrendChart entries={entries} locale="en-US" />)

    expect(container.querySelectorAll('[data-testid="trend-chart-week-tick"]').length).toBeGreaterThan(0)
  })

  it('suppresses weekly tick marks for a very long (> ~7 month) span', () => {
    const entries = [
      entry({ computedAtUtc: '2025-01-01T12:00:00+00:00' }),
      entry({ computedAtUtc: '2026-06-01T12:00:00+00:00' }),
    ]

    const { container } = render(<TrendChart entries={entries} locale="en-US" />)

    expect(container.querySelectorAll('[data-testid="trend-chart-week-tick"]')).toHaveLength(0)
  })

  it('renders fallback dated ticks instead of month ticks for a short (<1 month) span', () => {
    const entries = [
      entry({ computedAtUtc: '2026-08-24T12:00:00+00:00' }),
      entry({ computedAtUtc: '2026-08-27T12:00:00+00:00' }),
      entry({ computedAtUtc: '2026-08-30T12:00:00+00:00' }),
    ]

    const { container } = render(<TrendChart entries={entries} locale="en-US" />)

    expect(container.querySelectorAll('[data-testid="trend-chart-month-tick"]')).toHaveLength(0)
    // 6-day span is fully deterministic (> 4 days -> 4 ticks) — an exact count catches an
    // off-by-one in the day-threshold ladder that a loose bound wouldn't.
    expect(container.querySelectorAll('[data-testid="trend-chart-dated-tick"]')).toHaveLength(4)
    expect(container.textContent).toContain('Aug')
  })

  it('replaces a single in-range month boundary with fallback dated ticks (sparse-axis edge case)', () => {
    // Jan 31 -> Feb 2 contains exactly one month boundary (Feb 1) — on its own that reads as
    // "somewhere in February" with no sense of how much history surrounds it, so it's replaced
    // by (not layered with) the dated-tick fallback rather than rendering both.
    const entries = [
      entry({ computedAtUtc: '2026-01-31T12:00:00+00:00' }),
      entry({ computedAtUtc: '2026-02-02T12:00:00+00:00' }),
    ]

    const { container } = render(<TrendChart entries={entries} locale="en-US" />)

    expect(container.querySelectorAll('[data-testid="trend-chart-month-tick"]')).toHaveLength(0)
    expect(container.querySelectorAll('[data-testid="trend-chart-dated-tick"]').length).toBeGreaterThan(0)
  })

  it('renders a single fallback tick, not stacked duplicates, when every entry shares one timestamp', () => {
    const sharedTimestamp = '2026-08-01T00:00:00+00:00'
    const entries = [entry({ computedAtUtc: sharedTimestamp }), entry({ computedAtUtc: sharedTimestamp })]

    const { container } = render(<TrendChart entries={entries} locale="en-US" />)

    expect(container.querySelectorAll('[data-testid="trend-chart-dated-tick"]')).toHaveLength(1)
  })

  describe('weekly tick suppression threshold', () => {
    const baseTime = new Date('2026-01-01T00:00:00Z').getTime()

    it('keeps weekly ticks at exactly the 210-day suppression boundary', () => {
      const entries = [
        entry({ computedAtUtc: new Date(baseTime).toISOString() }),
        entry({ computedAtUtc: new Date(baseTime + 210 * DAY_MS).toISOString() }),
      ]

      const { container } = render(<TrendChart entries={entries} locale="en-US" />)

      expect(container.querySelectorAll('[data-testid="trend-chart-week-tick"]').length).toBeGreaterThan(0)
    })

    it('suppresses weekly ticks one day past the 210-day suppression boundary', () => {
      const entries = [
        entry({ computedAtUtc: new Date(baseTime).toISOString() }),
        entry({ computedAtUtc: new Date(baseTime + 211 * DAY_MS).toISOString() }),
      ]

      const { container } = render(<TrendChart entries={entries} locale="en-US" />)

      expect(container.querySelectorAll('[data-testid="trend-chart-week-tick"]')).toHaveLength(0)
    })
  })

  describe('measured container width', () => {
    const originalResizeObserver = globalThis.ResizeObserver

    afterEach(() => {
      ResizeObserverStub.instances = []
      globalThis.ResizeObserver = originalResizeObserver
    })

    it('adopts the ResizeObserver-measured width once it reports, replacing the fallback', () => {
      globalThis.ResizeObserver = ResizeObserverStub as unknown as typeof ResizeObserver
      const entries = [entry({ computedAtUtc: '2026-08-01T00:00:00+00:00' }), entry({ computedAtUtc: '2026-08-02T00:00:00+00:00' })]

      const { container } = render(<TrendChart entries={entries} locale="en-US" />)
      const svg = container.querySelector('svg')
      expect(svg).toHaveAttribute('viewBox', '0 0 640 130')

      act(() => {
        ResizeObserverStub.instances[0].trigger(900)
      })

      expect(svg).toHaveAttribute('viewBox', '0 0 900 130')
    })

    it('still measures the container when it first mounts on a later render (chart started as the empty state)', () => {
      // Regression: a plain useRef + useEffect(fn, []) attaches only once at first mount and never
      // notices a container that didn't exist in the tree yet — exactly this sequence (fetch
      // resolves after the initial empty-state render, same component instance, no remount).
      globalThis.ResizeObserver = ResizeObserverStub as unknown as typeof ResizeObserver
      const { container, rerender } = render(<TrendChart entries={[]} locale="en-US" />)
      expect(container.querySelector('svg')).toBeNull()

      rerender(
        <TrendChart
          entries={[entry({ computedAtUtc: '2026-08-01T00:00:00+00:00' }), entry({ computedAtUtc: '2026-08-02T00:00:00+00:00' })]}
          locale="en-US"
        />,
      )

      expect(ResizeObserverStub.instances.length).toBeGreaterThan(0)

      act(() => {
        ResizeObserverStub.instances[0].trigger(1200)
      })

      expect(container.querySelector('svg')).toHaveAttribute('viewBox', '0 0 1200 130')
    })

    it('never lets a very small width collapse or invert the x-axis scale', () => {
      globalThis.ResizeObserver = ResizeObserverStub as unknown as typeof ResizeObserver
      const entries = [entry({ computedAtUtc: '2026-08-01T00:00:00+00:00' }), entry({ computedAtUtc: '2026-08-02T00:00:00+00:00' })]

      const { container } = render(<TrendChart entries={entries} locale="en-US" />)

      act(() => {
        ResizeObserverStub.instances[0].trigger(10)
      })

      const point = container.querySelector('[data-testid="trend-chart-point"]')
      expect(Number(point?.getAttribute('cx'))).toBeGreaterThan(0)
      expect(Number.isFinite(Number(point?.getAttribute('cx')))).toBe(true)
    })
  })

  it('appends a 2-digit year to the first month label after a year change (multi-year span)', () => {
    const entries = [
      entry({ computedAtUtc: '2025-11-15T12:00:00+00:00' }),
      entry({ computedAtUtc: '2026-02-15T12:00:00+00:00' }),
    ]

    const { container } = render(<TrendChart entries={entries} locale="en-US" />)

    // Boundaries strictly inside: Dec 1 '25, Jan 1 '26, Feb 1 '26 — only the first label after the
    // year change (Jan) disambiguates with a 2-digit year; Dec and Feb don't repeat it.
    const labels = Array.from(container.querySelectorAll('[data-testid="trend-chart-month-tick"] text')).map((el) => el.textContent)
    expect(labels).toEqual(['Dec', 'Jan 26', 'Feb'])
  })
})
