import { render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { MeterReadingsCard } from './meter-readings-card'
import type { MeterReadingHistoryItemDto, MeterReadingHistoryPageDto } from '@/lib/meter-reading-history-api'

function jsonResponse(body: object | null, status = 200) {
  return new Response(body === null ? null : JSON.stringify(body), { status })
}

function item(overrides: Partial<MeterReadingHistoryItemDto> = {}): MeterReadingHistoryItemDto {
  return {
    id: '11111111-1111-1111-1111-111111111111',
    kwhValue: 4821.5,
    readingTimestamp: '2026-08-15T14:32:00+00:00',
    version: 0,
    isPendingRegression: false,
    correctedFromKwhValue: null,
    correctedAtUtc: null,
    ...overrides,
  }
}

function page(overrides: Partial<MeterReadingHistoryPageDto> = {}): MeterReadingHistoryPageDto {
  return {
    items: [item()],
    totalCount: 1,
    page: 1,
    pageSize: 20,
    ...overrides,
  }
}

async function openDisclosure(user: ReturnType<typeof userEvent.setup>) {
  await user.click(screen.getByText(/logged/))
}

describe('MeterReadingsCard', () => {
  afterEach(() => {
    vi.unstubAllGlobals()
  })

  it('shows a live count summary in the collapsed disclosure', async () => {
    vi.stubGlobal('fetch', vi.fn(() => Promise.resolve(jsonResponse(page({ totalCount: 214 })))))

    render(<MeterReadingsCard locale="en-US" />)

    expect(await screen.findByText('Meter Readings — 214 logged')).toBeInTheDocument()
  })

  it('renders a fetched page of readings once expanded', async () => {
    const user = userEvent.setup()
    vi.stubGlobal('fetch', vi.fn(() => Promise.resolve(jsonResponse(page()))))

    render(<MeterReadingsCard locale="en-US" />)
    await openDisclosure(user)

    expect(await screen.findByText('4,821.5 kWh')).toBeInTheDocument()
  })

  it('renders the empty state when totalCount is 0', async () => {
    const user = userEvent.setup()
    vi.stubGlobal('fetch', vi.fn(() => Promise.resolve(jsonResponse(page({ items: [], totalCount: 0 })))))

    render(<MeterReadingsCard locale="en-US" />)
    await openDisclosure(user)

    expect(await screen.findByText('No Meter Readings logged yet.')).toBeInTheDocument()
  })

  it('renders the Pending badge only for isPendingRegression rows', async () => {
    const user = userEvent.setup()
    vi.stubGlobal(
      'fetch',
      vi.fn(() =>
        Promise.resolve(
          jsonResponse(
            page({
              items: [
                item({ id: 'r1', kwhValue: 100, isPendingRegression: true }),
                item({ id: 'r2', kwhValue: 200, isPendingRegression: false }),
              ],
              totalCount: 2,
            }),
          ),
        ),
      ),
    )

    render(<MeterReadingsCard locale="en-US" />)
    await openDisclosure(user)

    await screen.findByText('100 kWh')
    expect(screen.getAllByText('Pending')).toHaveLength(1)
  })

  it('gives each row a distinct accessible name for its Edit button', async () => {
    const user = userEvent.setup()
    vi.stubGlobal(
      'fetch',
      vi.fn(() =>
        Promise.resolve(
          jsonResponse(
            page({
              items: [
                item({ id: 'r1', kwhValue: 100, readingTimestamp: '2026-08-15T14:32:00+00:00' }),
                item({ id: 'r2', kwhValue: 200, readingTimestamp: '2026-08-16T09:00:00+00:00' }),
              ],
              totalCount: 2,
            }),
          ),
        ),
      ),
    )

    render(<MeterReadingsCard locale="en-US" />)
    await openDisclosure(user)

    await screen.findByText('100 kWh')
    const editButtons = screen.getAllByRole('button', { name: /Edit reading from/ })
    expect(editButtons).toHaveLength(2)
    expect(editButtons[0].getAttribute('aria-label')).not.toBe(editButtons[1].getAttribute('aria-label'))
  })

  it('renders a correction note only when correctedFromKwhValue is non-null', async () => {
    const user = userEvent.setup()
    vi.stubGlobal(
      'fetch',
      vi.fn(() =>
        Promise.resolve(
          jsonResponse(
            page({
              items: [
                item({ id: 'r1', kwhValue: 100, correctedFromKwhValue: 90, correctedAtUtc: '2026-08-14T00:00:00+00:00' }),
                item({ id: 'r2', kwhValue: 200 }),
              ],
              totalCount: 2,
            }),
          ),
        ),
      ),
    )

    render(<MeterReadingsCard locale="en-US" />)
    await openDisclosure(user)

    expect(await screen.findByText('Originally logged as 90 kWh')).toBeInTheDocument()
  })

  it('disables Previous on the first page and Next on the last page, and paginates on click', async () => {
    const user = userEvent.setup()
    const fetchMock = vi.fn((input: string | URL | Request) => {
      const url = String(input)
      if (url.includes('page=2')) {
        return Promise.resolve(jsonResponse(page({ items: [item({ id: 'r2', kwhValue: 200 })], totalCount: 40, page: 2, pageSize: 20 })))
      }
      return Promise.resolve(jsonResponse(page({ items: [item({ id: 'r1', kwhValue: 100 })], totalCount: 40, page: 1, pageSize: 20 })))
    })
    vi.stubGlobal('fetch', fetchMock)

    render(<MeterReadingsCard locale="en-US" />)
    await openDisclosure(user)

    await screen.findByText('100 kWh')
    expect(screen.getByRole('button', { name: 'Previous' })).toBeDisabled()
    expect(screen.getByRole('button', { name: 'Next' })).not.toBeDisabled()

    await user.click(screen.getByRole('button', { name: 'Next' }))

    await screen.findByText('200 kWh')
    expect(screen.getByText('Page 2 of 2')).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Next' })).toBeDisabled()
    expect(screen.getByRole('button', { name: 'Previous' })).not.toBeDisabled()
  })

  it('renders an error state when the fetch fails', async () => {
    const user = userEvent.setup()
    vi.stubGlobal('fetch', vi.fn(() => Promise.resolve(new Response(JSON.stringify({ detail: 'boom' }), { status: 500 }))))

    render(<MeterReadingsCard locale="en-US" />)
    await openDisclosure(user)

    expect(await screen.findByText("Couldn't load the reading history — try again.")).toBeInTheDocument()
  })

  it('re-fetches the current page after a save via the edit dialog', async () => {
    const user = userEvent.setup()
    let saved = false
    const fetchMock = vi.fn((input: string | URL | Request) => {
      const url = String(input)
      if (url.includes('/api/meter-readings/') && !url.includes('page=')) {
        saved = true
        return Promise.resolve(jsonResponse(item({ kwhValue: 5000 })))
      }
      return Promise.resolve(jsonResponse(page({ items: [item({ kwhValue: saved ? 5000 : 4821.5 })] })))
    })
    vi.stubGlobal('fetch', fetchMock)

    render(<MeterReadingsCard locale="en-US" />)
    await openDisclosure(user)

    await screen.findByText('4,821.5 kWh')
    await user.click(screen.getByRole('button', { name: /Edit reading from/ }))
    await user.click(screen.getByRole('button', { name: 'Save' }))

    await screen.findByText('5,000 kWh')
    const getCalls = fetchMock.mock.calls.filter(([input]) => String(input).includes('/api/meter-readings?'))
    expect(getCalls.length).toBeGreaterThanOrEqual(2)
  })
})
