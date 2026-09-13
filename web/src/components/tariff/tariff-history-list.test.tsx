import { render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { TariffHistoryList } from './tariff-history-list'

function jsonResponse(body: object | null, status = 200) {
  return new Response(body === null ? null : JSON.stringify(body), { status })
}

describe('TariffHistoryList', () => {
  afterEach(() => {
    vi.unstubAllGlobals()
  })

  it('shows the empty state when there is no history yet', async () => {
    vi.stubGlobal('fetch', vi.fn(() => Promise.resolve(jsonResponse({ items: [], totalCount: 0, page: 1, pageSize: 20 }))))

    render(<TariffHistoryList locale="en-US" refreshNonce={0} />)

    expect(await screen.findByText('No Tariff entries yet — add your current contract above.')).toBeInTheDocument()
  })

  it('shows a load error when the fetch fails', async () => {
    vi.stubGlobal('fetch', vi.fn(() => Promise.resolve(new Response(null, { status: 500 }))))

    render(<TariffHistoryList locale="en-US" refreshNonce={0} />)

    expect(await screen.findByText("Couldn't load the Tariff history — try again.")).toBeInTheDocument()
  })

  it('renders an entry, flags the current one, and shows its correction note', async () => {
    const item = {
      id: '11111111-1111-1111-1111-111111111111',
      monthlyBaseFee: 12.5,
      pricePerKwh: 0.32,
      currency: 'EUR',
      contractStartDate: '2026-01-01T00:00:00+00:00',
      contractPeriodMonths: 12,
      version: 1,
      isCurrent: true,
      effectiveUntil: null,
      corrections: [
        { fieldName: 'MonthlyBaseFee', oldValue: '10', newValue: '12.5', correctedAtUtc: '2026-02-01T00:00:00+00:00' },
      ],
    }
    vi.stubGlobal('fetch', vi.fn(() => Promise.resolve(jsonResponse({ items: [item], totalCount: 1, page: 1, pageSize: 20 }))))

    render(<TariffHistoryList locale="en-US" refreshNonce={0} />)

    expect(await screen.findByText('Current')).toBeInTheDocument()
    expect(screen.getByText('Base fee originally 10')).toBeInTheDocument()
    expect(screen.getByText('12.5 EUR')).toBeInTheDocument()
  })

  it('a non-current entry shows its effective period ending at the next entry, no Current badge', async () => {
    const item = {
      id: '22222222-2222-2222-2222-222222222222',
      monthlyBaseFee: 10,
      pricePerKwh: 0.3,
      currency: 'EUR',
      contractStartDate: '2025-01-01T00:00:00+00:00',
      contractPeriodMonths: 12,
      version: 0,
      isCurrent: false,
      effectiveUntil: '2026-01-01T00:00:00+00:00',
      corrections: [],
    }
    vi.stubGlobal('fetch', vi.fn(() => Promise.resolve(jsonResponse({ items: [item], totalCount: 1, page: 1, pageSize: 20 }))))

    render(<TariffHistoryList locale="en-US" refreshNonce={0} />)

    await screen.findByText(/Jan 1, 2025/)
    expect(screen.queryByText('Current')).not.toBeInTheDocument()
  })

  it('clicking Edit opens the EditTariffDialog for that entry', async () => {
    const item = {
      id: '33333333-3333-3333-3333-333333333333',
      monthlyBaseFee: 12.5,
      pricePerKwh: 0.32,
      currency: 'EUR',
      contractStartDate: '2099-01-01T00:00:00+00:00',
      contractPeriodMonths: 12,
      version: 1,
      isCurrent: true,
      effectiveUntil: null,
      corrections: [],
    }
    vi.stubGlobal('fetch', vi.fn(() => Promise.resolve(jsonResponse({ items: [item], totalCount: 1, page: 1, pageSize: 20 }))))
    const user = userEvent.setup()

    render(<TariffHistoryList locale="en-US" refreshNonce={0} />)

    await user.click(await screen.findByRole('button', { name: /Edit Tariff entry starting/ }))

    expect(screen.getByRole('heading', { name: 'Edit Tariff entry' })).toBeInTheDocument()
  })

  it('pagination buttons are disabled appropriately for a single page', async () => {
    const item = {
      id: '44444444-4444-4444-4444-444444444444',
      monthlyBaseFee: 12.5,
      pricePerKwh: 0.32,
      currency: 'EUR',
      contractStartDate: '2026-01-01T00:00:00+00:00',
      contractPeriodMonths: 12,
      version: 0,
      isCurrent: true,
      effectiveUntil: null,
      corrections: [],
    }
    vi.stubGlobal('fetch', vi.fn(() => Promise.resolve(jsonResponse({ items: [item], totalCount: 1, page: 1, pageSize: 20 }))))

    render(<TariffHistoryList locale="en-US" refreshNonce={0} />)

    expect(await screen.findByText('Page 1 of 1')).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Previous' })).toBeDisabled()
    expect(screen.getByRole('button', { name: 'Next' })).toBeDisabled()
  })
})
