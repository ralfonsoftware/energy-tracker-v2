import { render, screen, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { TariffHistoryList } from './tariff-history-list'
import { stubWideViewport } from '@/test/wide-viewport'

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

describe('TariffHistoryList at >=660px (entry-grid)', () => {
  afterEach(() => {
    vi.unstubAllGlobals()
  })

  const base = {
    monthlyBaseFee: 12.5,
    pricePerKwh: 0.32,
    currency: 'EUR',
    contractPeriodMonths: 12,
    version: 1,
    corrections: [] as { fieldName: string; oldValue: string; newValue: string; correctedAtUtc: string }[],
  }
  const current = {
    ...base,
    id: 'c1',
    contractStartDate: '2026-01-01T00:00:00+00:00',
    isCurrent: true,
    effectiveUntil: null,
    corrections: [
      { fieldName: 'MonthlyBaseFee', oldValue: '10', newValue: '12.5', correctedAtUtc: '2026-02-01T00:00:00+00:00' },
      { fieldName: 'ContractStartDate', oldValue: '2025-12-15T00:00:00.0000000+00:00', newValue: '2026-01-01T00:00:00.0000000+00:00', correctedAtUtc: '2026-02-01T00:00:00+00:00' },
    ],
  }
  const older = {
    ...base,
    id: 'o1',
    monthlyBaseFee: 10,
    pricePerKwh: 0.3,
    contractStartDate: '2025-01-01T00:00:00+00:00',
    isCurrent: false,
    effectiveUntil: '2026-01-01T00:00:00+00:00',
  }

  function stubFetch(items: object[], extra?: (input: string, init?: RequestInit) => Response | null) {
    const fetchMock = vi.fn((input: string | URL | Request, init?: RequestInit) => {
      const custom = extra?.(String(input), init)
      return Promise.resolve(custom ?? jsonResponse({ items, totalCount: items.length, page: 1, pageSize: 20 }))
    })
    vi.stubGlobal('fetch', fetchMock)
    stubWideViewport()
    return fetchMock
  }

  it('renders entries as list-item tiles instead of a table', async () => {
    stubFetch([current, older])

    render(<TariffHistoryList locale="en-US" refreshNonce={0} />)

    await screen.findByText('Current')
    expect(screen.queryByRole('table')).not.toBeInTheDocument()
    expect(screen.getAllByRole('listitem')).toHaveLength(2)
  })

  it('shows period, Current badge, corrections, and fixed-decimal fee and price in the tile', async () => {
    stubFetch([current, older])

    render(<TariffHistoryList locale="en-US" refreshNonce={0} />)

    await screen.findByText('Current')
    const [currentTile, olderTile] = screen.getAllByRole('listitem')
    expect(within(currentTile).getByText('Current')).toBeInTheDocument()
    expect(within(currentTile).getByText(/Jan 1, 2026/)).toBeInTheDocument()
    expect(within(currentTile).getByText(/Ongoing/i)).toBeInTheDocument()
    expect(within(currentTile).getByText('Base fee originally 10.00')).toBeInTheDocument()
    expect(within(currentTile).getByText('Contract start date originally Dec 15, 2025')).toBeInTheDocument()
    expect(within(currentTile).getByText('12.50 EUR')).toBeInTheDocument()
    expect(within(currentTile).getByText('0.3200 EUR/kWh')).toBeInTheDocument()
    expect(within(olderTile).queryByText('Current')).not.toBeInTheDocument()
    expect(within(olderTile).getByText(/Jan 1, 2025/)).toBeInTheDocument()
    expect(within(olderTile).getByText('10.00 EUR')).toBeInTheDocument()
    expect(within(olderTile).getByText('0.3000 EUR/kWh')).toBeInTheDocument()
  })

  it('clicking a tile Edit opens the dialog; saving reloads and fires onTariffMutated', async () => {
    const item = { ...current, corrections: [], contractStartDate: '2099-01-01T00:00:00+00:00' }
    const fetchMock = stubFetch([item], (_url, init) =>
      (init?.method ?? 'GET').toUpperCase() === 'PUT' ? jsonResponse({ ...item, version: 2 }) : null,
    )
    const onTariffMutated = vi.fn()
    const user = userEvent.setup()

    render(<TariffHistoryList locale="en-US" refreshNonce={0} onTariffMutated={onTariffMutated} />)

    await user.click(await screen.findByRole('button', { name: /Edit Tariff entry starting/ }))
    expect(screen.getByRole('heading', { name: 'Edit Tariff entry' })).toBeInTheDocument()
    const loadsBefore = fetchMock.mock.calls.filter(([, init]) => (init?.method ?? 'GET') === 'GET').length
    await user.click(screen.getByRole('button', { name: 'Save' }))

    await vi.waitFor(() => expect(onTariffMutated).toHaveBeenCalledTimes(1))
    await vi.waitFor(() =>
      expect(fetchMock.mock.calls.filter(([, init]) => (init?.method ?? 'GET') === 'GET').length).toBeGreaterThan(loadsBefore),
    )
  })

  it('still renders and drives pagination below the grid', async () => {
    const user = userEvent.setup()
    const fetchMock = vi.fn((input: string | URL | Request) =>
      Promise.resolve(
        String(input).includes('page=2')
          ? jsonResponse({ items: [older], totalCount: 40, page: 2, pageSize: 20 })
          : jsonResponse({ items: [current], totalCount: 40, page: 1, pageSize: 20 }),
      ),
    )
    vi.stubGlobal('fetch', fetchMock)
    stubWideViewport()

    render(<TariffHistoryList locale="en-US" refreshNonce={0} />)

    expect(await screen.findByText('Page 1 of 2')).toBeInTheDocument()
    await user.click(screen.getByRole('button', { name: 'Next' }))

    expect(await screen.findByText('Page 2 of 2')).toBeInTheDocument()
    expect(screen.getAllByRole('listitem')).toHaveLength(1)
    expect(screen.getByRole('button', { name: 'Next' })).toBeDisabled()
  })
})
