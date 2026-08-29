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
      return Promise.resolve(jsonResponse(null))
    }),
  )
}

describe('TrendHistoryPage', () => {
  afterEach(() => {
    vi.unstubAllGlobals()
  })

  it('renders the chart and the Meter Readings card', async () => {
    mockRoutes([])

    render(<TrendHistoryPage locale="en-US" onBack={() => {}} onSettingsClick={() => {}} onSmartPlugImportClick={() => {}} />)

    expect(await screen.findByText('Not enough history yet to show a trend.')).toBeInTheDocument()
    expect(await screen.findByText('Meter Readings — 0 logged')).toBeInTheDocument()
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
})
