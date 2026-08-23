import { useState } from 'react'
import { render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { StatusDetailDialog } from './status-detail-dialog'
import { ApiError, type StatusDetailDto } from '@/lib/status-api'

function detailDto(overrides: Partial<StatusDetailDto> = {}): StatusDetailDto {
  return {
    status: 'withinRange',
    paceToDateKwh: 1060,
    baselineToDateKwh: 1300,
    elapsedDays: 182.5,
    trendingThresholdKwh: 100,
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
function Harness() {
  const [open, setOpen] = useState(false)
  return (
    <StatusDetailDialog
      open={open}
      onOpenChange={setOpen}
      locale="en-US"
      trigger={<button>How was this calculated?</button>}
    />
  )
}

describe('StatusDetailDialog', () => {
  afterEach(() => {
    vi.unstubAllGlobals()
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
