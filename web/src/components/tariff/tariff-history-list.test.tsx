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

  it('clicking Retry after a load error re-fetches and reports the result via onLoaded', async () => {
    const page = { items: [], totalCount: 0, page: 1, pageSize: 20 }
    const fetchMock = vi
      .fn()
      .mockResolvedValueOnce(new Response(null, { status: 500 }))
      .mockResolvedValueOnce(jsonResponse(page))
    vi.stubGlobal('fetch', fetchMock)
    const onLoaded = vi.fn()
    const user = userEvent.setup()

    render(<TariffHistoryList locale="en-US" refreshNonce={0} onLoaded={onLoaded} />)
    await screen.findByText("Couldn't load the Tariff history — try again.")
    expect(onLoaded).not.toHaveBeenCalled()

    await user.click(screen.getByRole('button', { name: 'Retry' }))

    expect(await screen.findByText('No Tariff entries yet — add your current contract above.')).toBeInTheDocument()
    expect(onLoaded).toHaveBeenCalledWith(page)
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
    expect(screen.getByText('Base fee originally 10.00')).toBeInTheDocument()
    expect(screen.getByText('12.50 EUR')).toBeInTheDocument()
  })

  it('formats a ContractStartDate correction note as a localized date, not a raw ISO timestamp', async () => {
    const item = {
      id: '55555555-5555-5555-5555-555555555555',
      monthlyBaseFee: 12.5,
      pricePerKwh: 0.32,
      currency: 'EUR',
      contractStartDate: '2026-02-01T00:00:00+00:00',
      contractPeriodMonths: 12,
      version: 1,
      isCurrent: true,
      effectiveUntil: null,
      corrections: [
        {
          fieldName: 'ContractStartDate',
          oldValue: '2026-01-15T00:00:00.0000000+00:00',
          newValue: '2026-02-01T00:00:00.0000000+00:00',
          correctedAtUtc: '2026-02-01T00:00:00+00:00',
        },
      ],
    }
    vi.stubGlobal('fetch', vi.fn(() => Promise.resolve(jsonResponse({ items: [item], totalCount: 1, page: 1, pageSize: 20 }))))

    render(<TariffHistoryList locale="en-US" refreshNonce={0} />)

    expect(await screen.findByText('Contract start date originally Jan 15, 2026')).toBeInTheDocument()
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

  it('saving an edit reloads the list and fires onTariffMutated (Story 5.4 AC #5)', async () => {
    const item = {
      id: '66666666-6666-6666-6666-666666666666',
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
    const page = { items: [item], totalCount: 1, page: 1, pageSize: 20 }
    const fetchMock = vi.fn((input: string | URL | Request, init?: RequestInit) => {
      const url = String(input)
      const method = (init?.method ?? 'GET').toUpperCase()
      if (method === 'PUT') {
        return Promise.resolve(jsonResponse({ ...item, version: 2 }))
      }
      if (url.startsWith('/api/tariffs')) {
        return Promise.resolve(jsonResponse(page))
      }
      return Promise.resolve(new Response(null, { status: 200 }))
    })
    vi.stubGlobal('fetch', fetchMock)
    const onTariffMutated = vi.fn()
    const user = userEvent.setup()

    render(<TariffHistoryList locale="en-US" refreshNonce={0} onTariffMutated={onTariffMutated} />)

    await user.click(await screen.findByRole('button', { name: /Edit Tariff entry starting/ }))
    await user.click(screen.getByRole('button', { name: 'Save' }))

    await vi.waitFor(() => expect(onTariffMutated).toHaveBeenCalledTimes(1))
    expect(fetchMock).toHaveBeenCalledWith(expect.stringMatching(/^\/api\/tariffs\?/), expect.anything())
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
