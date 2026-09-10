import { render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { TrendHistoryPage } from './trend-history-page'

function jsonResponse(body: object | null, status = 200) {
  return new Response(body === null ? null : JSON.stringify(body), { status })
}

function mockRoutes(historyEntries: unknown[] = []) {
  vi.stubGlobal(
    'fetch',
    vi.fn((input: string | URL | Request) => {
      const url = String(input)
      if (url === '/api/status/history') {
        return Promise.resolve(jsonResponse(historyEntries))
      }
      if (url.startsWith('/api/meter-readings')) {
        return Promise.resolve(jsonResponse({ items: [], totalCount: 0, page: 1, pageSize: 20 }))
      }
      if (url === '/api/smart-plug-readings') {
        return Promise.resolve(jsonResponse([]))
      }
      return Promise.resolve(jsonResponse(null))
    }),
  )
}

describe('TrendHistoryPage', () => {
  afterEach(() => {
    vi.unstubAllGlobals()
  })

  it('renders the chart, the Meter Readings card, and the Per-Plug card in that order', async () => {
    mockRoutes([])

    render(<TrendHistoryPage locale="en-US" onBack={() => {}} onSettingsClick={() => {}} onSmartPlugImportClick={() => {}} />)

    expect(await screen.findByText('Not enough history yet to show a trend.')).toBeInTheDocument()
    expect(await screen.findByText('Meter Readings — 0 logged')).toBeInTheDocument()
    const perPlugHeading = await screen.findByText('Room → Power Point → Device')
    const readingsSummary = screen.getByText('Meter Readings — 0 logged')
    expect(readingsSummary.compareDocumentPosition(perPlugHeading) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy()
  })

  it('renders the Smart Plug Import icon button and calls onSmartPlugImportClick', async () => {
    mockRoutes([])
    const user = userEvent.setup()
    const onSmartPlugImportClick = vi.fn()

    render(<TrendHistoryPage locale="en-US" onBack={() => {}} onSettingsClick={() => {}} onSmartPlugImportClick={onSmartPlugImportClick} />)

    const trigger = screen.getByRole('button', { name: 'Import Smart Plug data' })
    await user.click(trigger)
    expect(onSmartPlugImportClick).toHaveBeenCalledOnce()
  })

  it('renders NavChrome with active="trendHistory" and Dashboard tap calls onBack', async () => {
    mockRoutes([])
    const user = userEvent.setup()
    const onBack = vi.fn()

    render(<TrendHistoryPage locale="en-US" onBack={onBack} onSettingsClick={() => {}} onSmartPlugImportClick={() => {}} />)

    const trendHistoryTab = await screen.findByRole('button', { name: 'Trend History' })
    expect(trendHistoryTab).toHaveAttribute('aria-current', 'page')

    await user.click(screen.getByRole('button', { name: 'Dashboard' }))
    expect(onBack).toHaveBeenCalledOnce()
  })

  it('calls onSettingsClick when the Settings tab is tapped', async () => {
    mockRoutes([])
    const user = userEvent.setup()
    const onSettingsClick = vi.fn()

    render(<TrendHistoryPage locale="en-US" onBack={() => {}} onSettingsClick={onSettingsClick} onSmartPlugImportClick={() => {}} />)

    await user.click(await screen.findByRole('button', { name: 'Settings' }))
    expect(onSettingsClick).toHaveBeenCalledOnce()
  })

  it('re-fetches Status history after a Meter Reading correction is saved (AC #3)', async () => {
    const user = userEvent.setup()
    const readingItem = {
      id: '11111111-1111-1111-1111-111111111111',
      kwhValue: 100,
      readingTimestamp: '2026-08-15T14:32:00+00:00',
      version: 0,
      isPendingRegression: false,
      correctedFromKwhValue: null,
      correctedAtUtc: null,
    }
    let saved = false
    const fetchMock = vi.fn((input: string | URL | Request) => {
      const url = String(input)
      if (url === '/api/status/history') {
        return Promise.resolve(jsonResponse([]))
      }
      if (url.includes('/api/meter-readings/') && !url.includes('?')) {
        saved = true
        return Promise.resolve(jsonResponse({ ...readingItem, kwhValue: 150, version: 1 }))
      }
      if (url.startsWith('/api/meter-readings')) {
        return Promise.resolve(
          jsonResponse({ items: [{ ...readingItem, kwhValue: saved ? 150 : 100 }], totalCount: 1, page: 1, pageSize: 20 }),
        )
      }
      if (url === '/api/smart-plug-readings') {
        return Promise.resolve(jsonResponse([]))
      }
      return Promise.resolve(jsonResponse(null))
    })
    vi.stubGlobal('fetch', fetchMock)

    render(<TrendHistoryPage locale="en-US" onBack={() => {}} onSettingsClick={() => {}} onSmartPlugImportClick={() => {}} />)

    await screen.findByText('Meter Readings — 1 logged')
    const historyCallsBeforeEdit = fetchMock.mock.calls.filter(([input]) => String(input) === '/api/status/history').length

    await user.click(screen.getByText('Meter Readings — 1 logged'))
    await user.click(await screen.findByRole('button', { name: /Edit reading from/ }))
    await user.click(screen.getByRole('button', { name: 'Save' }))

    await screen.findByText('150 kWh')
    const historyCallsAfterEdit = fetchMock.mock.calls.filter(([input]) => String(input) === '/api/status/history').length
    expect(historyCallsAfterEdit).toBeGreaterThan(historyCallsBeforeEdit)
  })
})
