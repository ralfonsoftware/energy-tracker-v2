import { render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { MeterReadingHistoryPage } from './meter-reading-history-page'
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

describe('MeterReadingHistoryPage', () => {
  afterEach(() => {
    vi.unstubAllGlobals()
  })

  it('renders a fetched page of readings', async () => {
    vi.stubGlobal('fetch', vi.fn(() => Promise.resolve(jsonResponse(page()))))

    render(<MeterReadingHistoryPage locale="en-US" onBack={() => {}} />)

    expect(await screen.findByText('4,821.5 kWh')).toBeInTheDocument()
  })

  it('renders the empty state when totalCount is 0', async () => {
    vi.stubGlobal('fetch', vi.fn(() => Promise.resolve(jsonResponse(page({ items: [], totalCount: 0 })))))

    render(<MeterReadingHistoryPage locale="en-US" onBack={() => {}} />)

    expect(await screen.findByText('No Meter Readings logged yet.')).toBeInTheDocument()
  })

  it('renders the Pending badge only for isPendingRegression rows', async () => {
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

    render(<MeterReadingHistoryPage locale="en-US" onBack={() => {}} />)

    await screen.findByText('100 kWh')
    expect(screen.getAllByText('Pending')).toHaveLength(1)
  })

  it('renders a correction note only when correctedFromKwhValue is non-null', async () => {
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

    render(<MeterReadingHistoryPage locale="en-US" onBack={() => {}} />)

    expect(await screen.findByText('Originally logged as 90 kWh')).toBeInTheDocument()
  })

  it('gives each row a distinct accessible name for its Edit button', async () => {
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

    render(<MeterReadingHistoryPage locale="en-US" onBack={() => {}} />)

    await screen.findByText('100 kWh')
    const editButtons = screen.getAllByRole('button', { name: /Edit reading from/ })
    expect(editButtons).toHaveLength(2)
    expect(editButtons[0].getAttribute('aria-label')).not.toBe(editButtons[1].getAttribute('aria-label'))
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

    render(<MeterReadingHistoryPage locale="en-US" onBack={() => {}} />)

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
    vi.stubGlobal('fetch', vi.fn(() => Promise.resolve(new Response(JSON.stringify({ detail: 'boom' }), { status: 500 }))))

    render(<MeterReadingHistoryPage locale="en-US" onBack={() => {}} />)

    expect(await screen.findByText("Couldn't load the reading history — try again.")).toBeInTheDocument()
  })

  it('the Back button calls onBack', async () => {
    vi.stubGlobal('fetch', vi.fn(() => Promise.resolve(jsonResponse(page()))))
    const user = userEvent.setup()
    const onBack = vi.fn()

    render(<MeterReadingHistoryPage locale="en-US" onBack={onBack} />)

    await waitFor(() => expect(screen.getByRole('button', { name: 'Go to Energy Tracker' })).toBeInTheDocument())
    await user.click(screen.getByRole('button', { name: 'Go to Energy Tracker' }))
    expect(onBack).toHaveBeenCalledOnce()
  })
})
