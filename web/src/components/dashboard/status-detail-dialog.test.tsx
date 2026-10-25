import { useState } from 'react'
import { render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import i18next from 'i18next'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { StatusDetailDialog } from './status-detail-dialog'
import { type StatusDetailDto } from '@/lib/status-api'
import { ApiError } from '@/lib/api-client'

function detailDto(overrides: Partial<StatusDetailDto> = {}): StatusDetailDto {
  return {
    status: 'withinRange',
    paceToDateKwh: 1060,
    baselineToDateKwh: 1300,
    elapsedDays: 182.5,
    trendingThresholdKwh: 100,
    trendingThresholdToDateKwh: 50,
    isLowConfidence: false,
    daysSinceLastReading: 1.2,
    lowConfidenceGapDaysThreshold: 45,
    ...overrides,
  }
}

function jsonResponse(body: object | null, status = 200) {
  return new Response(body === null ? null : JSON.stringify(body), { status })
}

// Mirrors the controlled-open shape DashboardPage drives in real usage — `open` is owned by the
// caller, not the dialog itself.
function Harness({ locale = 'en-US' }: { locale?: string }) {
  const [open, setOpen] = useState(false)
  return (
    <StatusDetailDialog
      open={open}
      onOpenChange={setOpen}
      locale={locale}
      trigger={<button>How was this calculated?</button>}
    />
  )
}

describe('StatusDetailDialog', () => {
  afterEach(async () => {
    vi.unstubAllGlobals()
    await i18next.changeLanguage('en-US')
  })

  it('opening the trigger fetches and renders the figure rows with locale-aware formatting', async () => {
    vi.stubGlobal('fetch', vi.fn(() => Promise.resolve(jsonResponse(detailDto()))))
    const user = userEvent.setup()
    render(<Harness />)

    await user.click(screen.getByRole('button', { name: 'How was this calculated?' }))

    expect(await screen.findByText('1,060 kWh')).toBeInTheDocument()
    expect(screen.getByText(/1,300 kWh/)).toBeInTheDocument()
    expect(screen.getByText(/over 182 days/)).toBeInTheDocument()
    expect(screen.getByText('240 kWh under pace.')).toBeInTheDocument()
    expect(screen.getByText('100 kWh')).toBeInTheDocument()
  })

  // Story 11.15 (audit C12, FR-6): the configured threshold is per year, the dialog also shows the
  // threshold scaled to the period the readings cover. Rows are looked up by their label so a
  // swapped value fails.
  function valueOfRow(label: string) {
    const row = screen.getByText(label).parentElement
    expect(row).not.toBeNull()
    return row as HTMLElement
  }

  it('shows the configured per-year threshold and the scaled threshold as two labelled rows', async () => {
    vi.stubGlobal('fetch', vi.fn(() => Promise.resolve(jsonResponse(detailDto({ trendingThresholdKwh: 100, trendingThresholdToDateKwh: 50 })))))
    const user = userEvent.setup()
    render(<Harness />)

    await user.click(screen.getByRole('button', { name: 'How was this calculated?' }))

    await screen.findByText('1,060 kWh')
    expect(valueOfRow('Trending threshold (per year)')).toHaveTextContent('100 kWh')
    expect(valueOfRow('Threshold for this period')).toHaveTextContent('50 kWh')
  })

  it('formats the scaled threshold with up to one decimal and no trailing .0', async () => {
    vi.stubGlobal('fetch', vi.fn(() => Promise.resolve(jsonResponse(detailDto({ trendingThresholdToDateKwh: 16.4 })))))
    const user = userEvent.setup()
    const { unmount } = render(<Harness />)
    await user.click(screen.getByRole('button', { name: 'How was this calculated?' }))
    await screen.findByText('1,060 kWh')
    expect(valueOfRow('Threshold for this period')).toHaveTextContent(/^Threshold for this period16\.4 kWh$/)
    unmount()

    vi.stubGlobal('fetch', vi.fn(() => Promise.resolve(jsonResponse(detailDto({ trendingThresholdToDateKwh: 20 })))))
    render(<Harness />)
    await user.click(screen.getByRole('button', { name: 'How was this calculated?' }))
    await screen.findByText('1,060 kWh')
    expect(valueOfRow('Threshold for this period')).toHaveTextContent(/^Threshold for this period20 kWh$/)
  })

  it('shows a sub-kWh scaled threshold instead of rounding it to 0 kWh', async () => {
    vi.stubGlobal('fetch', vi.fn(() => Promise.resolve(jsonResponse(detailDto({ trendingThresholdToDateKwh: 0.3 })))))
    const user = userEvent.setup()
    render(<Harness />)

    await user.click(screen.getByRole('button', { name: 'How was this calculated?' }))

    await screen.findByText('1,060 kWh')
    expect(valueOfRow('Threshold for this period')).toHaveTextContent('0.3 kWh')
  })

  it('keeps two significant digits for a scaled threshold under 0.05 kWh (a span of a few hours)', async () => {
    vi.stubGlobal('fetch', vi.fn(() => Promise.resolve(jsonResponse(detailDto({ trendingThresholdToDateKwh: 0.0114 })))))
    const user = userEvent.setup()
    render(<Harness />)

    await user.click(screen.getByRole('button', { name: 'How was this calculated?' }))

    await screen.findByText('1,060 kWh')
    expect(valueOfRow('Threshold for this period')).toHaveTextContent('0.011 kWh')
  })

  it('shows the difference with decimals when the scaled threshold is under 10 kWh, so it never reads 0 kWh beside Trending', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn(() =>
        Promise.resolve(
          jsonResponse(detailDto({ paceToDateKwh: 1000.4, baselineToDateKwh: 1000, trendingThresholdToDateKwh: 0.3, status: 'trending' })),
        ),
      ),
    )
    const user = userEvent.setup()
    render(<Harness />)

    await user.click(screen.getByRole('button', { name: 'How was this calculated?' }))

    expect(await screen.findByText('0.4 kWh over pace.')).toBeInTheDocument()
  })

  it('keeps the difference in whole kWh once the scaled threshold is 10 kWh or more', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn(() =>
        Promise.resolve(jsonResponse(detailDto({ paceToDateKwh: 1020.4, baselineToDateKwh: 1000, trendingThresholdToDateKwh: 20, status: 'trending' }))),
      ),
    )
    const user = userEvent.setup()
    render(<Harness />)

    await user.click(screen.getByRole('button', { name: 'How was this calculated?' }))

    expect(await screen.findByText('20 kWh over pace.')).toBeInTheDocument()
  })

  it('renders the German labels and a comma decimal in de-DE', async () => {
    await i18next.changeLanguage('de-DE')
    vi.stubGlobal('fetch', vi.fn(() => Promise.resolve(jsonResponse(detailDto({ trendingThresholdToDateKwh: 16.4 })))))
    const user = userEvent.setup()
    render(<Harness locale="de-DE" />)

    // The harness trigger is a fixed English button; only the dialog's own copy is translated.
    await user.click(screen.getByRole('button', { name: 'How was this calculated?' }))

    expect(await screen.findByText('Schwellenwert für Anstieg (pro Jahr)')).toBeInTheDocument()
    expect(valueOfRow('Schwellenwert für Anstieg (pro Jahr)')).toHaveTextContent('100 kWh')
    expect(valueOfRow('Schwellenwert für diesen Zeitraum')).toHaveTextContent('16,4 kWh')
  })

  it('omits the scaled row instead of printing NaN when the response lacks the field (a rolled-back API)', async () => {
    const withoutScaled = { ...detailDto() } as Partial<StatusDetailDto>
    delete withoutScaled.trendingThresholdToDateKwh
    vi.stubGlobal('fetch', vi.fn(() => Promise.resolve(jsonResponse(withoutScaled as object))))
    const user = userEvent.setup()
    render(<Harness />)

    await user.click(screen.getByRole('button', { name: 'How was this calculated?' }))

    await screen.findByText('1,060 kWh')
    expect(valueOfRow('Trending threshold (per year)')).toHaveTextContent('100 kWh')
    expect(screen.queryByText('Threshold for this period')).not.toBeInTheDocument()
    expect(screen.queryByText(/NaN/)).not.toBeInTheDocument()
  })

  it('shows a loading skeleton before the fetch resolves', async () => {
    let resolveFetch: (value: Response) => void = () => {}
    vi.stubGlobal(
      'fetch',
      vi.fn(
        () =>
          new Promise<Response>((resolve) => {
            resolveFetch = resolve
          }),
      ),
    )
    const user = userEvent.setup()
    render(<Harness />)

    await user.click(screen.getByRole('button', { name: 'How was this calculated?' }))

    expect(screen.getByText('Status calculation').closest('[data-slot="dialog-content"]')?.querySelectorAll('[data-slot="skeleton"]').length).toBeGreaterThan(0)

    resolveFetch(jsonResponse(detailDto()))
    await waitFor(() => expect(screen.getByText('1,060 kWh')).toBeInTheDocument())
  })

  it('renders the low-confidence explanation only when isLowConfidence is true', async () => {
    vi.stubGlobal('fetch', vi.fn(() => Promise.resolve(jsonResponse(detailDto({ isLowConfidence: true, daysSinceLastReading: 50 })))))
    const user = userEvent.setup()
    render(<Harness />)

    await user.click(screen.getByRole('button', { name: 'How was this calculated?' }))

    expect(await screen.findByText(/50 days since your last reading/)).toBeInTheDocument()
  })

  it('never displays a day count equal to the threshold at the low-confidence rounding boundary', async () => {
    // daysSinceLastReading is only ever isLowConfidence-true when strictly greater than the
    // (integer) threshold — 45.4 rounds to 45 with Math.round, which would read as "45 days...
    // more than the household's 45-day threshold" (self-contradictory). Ceiling must avoid it.
    vi.stubGlobal(
      'fetch',
      vi.fn(() => Promise.resolve(jsonResponse(detailDto({ isLowConfidence: true, daysSinceLastReading: 45.4, lowConfidenceGapDaysThreshold: 45 })))),
    )
    const user = userEvent.setup()
    render(<Harness />)

    await user.click(screen.getByRole('button', { name: 'How was this calculated?' }))

    expect(await screen.findByText(/46 days since your last reading/)).toBeInTheDocument()
    expect(screen.getByText(/more than the household's 45-day threshold/)).toBeInTheDocument()
  })

  it('does not render a low-confidence explanation when isLowConfidence is false', async () => {
    vi.stubGlobal('fetch', vi.fn(() => Promise.resolve(jsonResponse(detailDto({ isLowConfidence: false })))))
    const user = userEvent.setup()
    render(<Harness />)

    await user.click(screen.getByRole('button', { name: 'How was this calculated?' }))

    await screen.findByText('1,060 kWh')
    expect(screen.queryByText(/days since your last reading/)).not.toBeInTheDocument()
  })

  it('renders an error line without an unhandled rejection when the fetch fails', async () => {
    vi.stubGlobal('fetch', vi.fn(() => Promise.resolve(new Response(JSON.stringify({ detail: 'boom' }), { status: 500 }))))
    const user = userEvent.setup()
    render(<Harness />)

    await user.click(screen.getByRole('button', { name: 'How was this calculated?' }))

    expect(await screen.findByText("Couldn't load the calculation detail — try again.")).toBeInTheDocument()
  })

  it('handles a thrown ApiError from fetch the same way as any other failure', async () => {
    vi.stubGlobal('fetch', vi.fn(() => Promise.reject(new ApiError(500, null))))
    const user = userEvent.setup()
    render(<Harness />)

    await user.click(screen.getByRole('button', { name: 'How was this calculated?' }))

    expect(await screen.findByText("Couldn't load the calculation detail — try again.")).toBeInTheDocument()
  })

  it('re-opening after a close re-fetches rather than showing stale data', async () => {
    const fetchMock = vi
      .fn()
      .mockResolvedValueOnce(jsonResponse(detailDto({ paceToDateKwh: 1060 })))
      .mockResolvedValueOnce(jsonResponse(detailDto({ paceToDateKwh: 2000 })))
    vi.stubGlobal('fetch', fetchMock)
    const user = userEvent.setup()
    render(<Harness />)

    await user.click(screen.getByRole('button', { name: 'How was this calculated?' }))
    expect(await screen.findByText('1,060 kWh')).toBeInTheDocument()

    // Two "Close" buttons exist (the footer button and the built-in dialog X) — the footer one
    // renders first in DOM order.
    await user.click(screen.getAllByRole('button', { name: 'Close' })[0])
    expect(screen.queryByText('1,060 kWh')).not.toBeInTheDocument()

    await user.click(screen.getByRole('button', { name: 'How was this calculated?' }))
    expect(await screen.findByText('2,000 kWh')).toBeInTheDocument()
    expect(fetchMock).toHaveBeenCalledTimes(2)
  })
})
