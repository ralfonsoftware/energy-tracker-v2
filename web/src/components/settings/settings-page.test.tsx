import 'fake-indexeddb/auto'
import { render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { IDBFactory } from 'fake-indexeddb'
import { SettingsPage } from './settings-page'
import { enqueue } from '@/lib/offline-queue'

function jsonResponse(body: object | null, status = 200) {
  return new Response(body === null ? null : JSON.stringify(body), { status })
}

const householdId = '11111111-1111-1111-1111-111111111111'

function stubFetch(meterReadingsResponse: () => Response = () => jsonResponse(null)) {
  vi.stubGlobal(
    'fetch',
    vi.fn((input: string | URL | Request) => {
      const url = String(input)
      if (url === '/api/rooms' || url === '/api/power-points' || url === '/api/devices') {
        return Promise.resolve(jsonResponse([]))
      }
      if (url === `/api/households/${householdId}`) {
        return Promise.resolve(jsonResponse({ id: householdId, locale: 'en-US', currency: 'USD', yearlyBaselineKwh: null, version: 0 }))
      }
      if (url === `/api/households/${householdId}/ai-plausibility`) {
        return Promise.resolve(jsonResponse({ enabled: false, backendConfigured: false, backendLabel: null, version: 0 }))
      }
      if (url === '/api/meter-readings') {
        return Promise.resolve(meterReadingsResponse())
      }
      return Promise.resolve(jsonResponse(null))
    }),
  )
}

function renderSettingsPage(supportsFederatedLogout: boolean) {
  render(
    <SettingsPage
      householdId={householdId}
      supportsFederatedLogout={supportsFederatedLogout}
      onBack={() => {}}
      onTrendHistoryClick={() => {}}
      onTariffRadarClick={() => {}}
    />,
  )
}

describe('SettingsPage', () => {
  beforeEach(() => {
    // jsdom has no native IndexedDB implementation — fake-indexeddb/auto stubs `indexedDB`
    // globally. Reset it between tests so each test starts from an empty offline queue.
    globalThis.indexedDB = new IDBFactory()
  })

  afterEach(() => {
    vi.unstubAllGlobals()
  })

  it('no longer renders a Smart Plug Import panel — moved to its own Dashboard-launched screen (Story 3.5, AC #1)', async () => {
    stubFetch()

    renderSettingsPage(true)

    expect(await screen.findByRole('heading', { name: 'Settings' })).toBeInTheDocument()
    expect(screen.queryByText('Smart Plug Import')).not.toBeInTheDocument()
    expect(screen.queryByText('Drop a file here, or choose one to upload.')).not.toBeInTheDocument()
  })

  it('threads onTrendHistoryClick through to the NavChrome Trend History tab (Story 4.1)', async () => {
    const user = userEvent.setup()
    const onTrendHistoryClick = vi.fn()
    vi.stubGlobal(
      'fetch',
      vi.fn((input: string | URL | Request) => {
        const url = String(input)
        if (url === '/api/rooms' || url === '/api/power-points' || url === '/api/devices') {
          return Promise.resolve(jsonResponse([]))
        }
        if (url === `/api/households/${householdId}`) {
          return Promise.resolve(jsonResponse({ id: householdId, locale: 'en-US', currency: 'USD', yearlyBaselineKwh: null, version: 0 }))
        }
        if (url === `/api/households/${householdId}/ai-plausibility`) {
          return Promise.resolve(jsonResponse({ enabled: false, backendConfigured: false, backendLabel: null, version: 0 }))
        }
        return Promise.resolve(jsonResponse(null))
      }),
    )

    render(
      <SettingsPage
        householdId={householdId}
        supportsFederatedLogout={true}
        onBack={() => {}}
        onTrendHistoryClick={onTrendHistoryClick}
        onTariffRadarClick={() => {}}
      />,
    )

    await user.click(await screen.findByRole('button', { name: 'Trend History' }))
    expect(onTrendHistoryClick).toHaveBeenCalledOnce()
  })

  describe('Logoff control (Story 1.12, FR-33)', () => {
    function stubLocation() {
      const originalLocation = window.location
      const mockLocation = { ...originalLocation, href: '' }
      Object.defineProperty(window, 'location', { value: mockLocation, writable: true })
      return () => Object.defineProperty(window, 'location', { value: originalLocation, writable: true })
    }

    it('is visible and reachable directly from Settings (AC #1)', async () => {
      stubFetch()

      renderSettingsPage(true)

      expect(await screen.findByRole('button', { name: 'Log off' })).toBeInTheDocument()
    })

    it('opens a confirmation dialog before doing anything else', async () => {
      const user = userEvent.setup()
      stubFetch()
      renderSettingsPage(true)

      await user.click(await screen.findByRole('button', { name: 'Log off' }))

      expect(await screen.findByRole('heading', { name: 'Log off?' })).toBeInTheDocument()
    })

    it('navigates straight to /logout when federated logout is supported and no readings are queued (AC #2, #6)', async () => {
      const user = userEvent.setup()
      stubFetch()
      const restoreLocation = stubLocation()
      renderSettingsPage(true)

      await user.click(await screen.findByRole('button', { name: 'Log off' }))
      await user.click(await screen.findByRole('button', { name: 'Yes, log off' }))

      await waitFor(() => expect(window.location.href).toBe('/logout'))
      restoreLocation()
    })

    it('shows a warning instead of navigating when federated logout is not supported, and proceeds only after Continue (AC #3)', async () => {
      const user = userEvent.setup()
      stubFetch()
      const restoreLocation = stubLocation()
      renderSettingsPage(false)

      await user.click(await screen.findByRole('button', { name: 'Log off' }))
      await user.click(await screen.findByRole('button', { name: 'Yes, log off' }))

      expect(await screen.findByRole('heading', { name: 'You may still be signed in elsewhere' })).toBeInTheDocument()
      expect(window.location.href).toBe('')

      await user.click(screen.getByRole('button', { name: 'Continue' }))
      await waitFor(() => expect(window.location.href).toBe('/logout'))
      restoreLocation()
    })

    it('surfaces still-queued readings instead of silently discarding them, and only navigates after an explicit choice (AC #4)', async () => {
      const user = userEvent.setup()
      // The flush attempt fails (simulating offline/transient failure) — the queued reading
      // survives, which is exactly what AC #4 requires to be surfaced explicitly.
      stubFetch(() => jsonResponse({ detail: 'unreachable' }, 503))
      await enqueue({ householdId, kwhValue: 4821.5, readingTimestamp: '2026-08-15T14:32:00Z', idempotencyKey: 'key-1' })
      const restoreLocation = stubLocation()
      renderSettingsPage(true)

      await user.click(await screen.findByRole('button', { name: 'Log off' }))
      await user.click(await screen.findByRole('button', { name: 'Yes, log off' }))

      expect(await screen.findByRole('heading', { name: 'Unsynced readings' })).toBeInTheDocument()
      expect(screen.getByText(/1 Meter Reading is still waiting to sync/)).toBeInTheDocument()
      expect(window.location.href).toBe('')

      await user.click(screen.getByRole('button', { name: 'Log off anyway' }))
      await waitFor(() => expect(window.location.href).toBe('/logout'))
      restoreLocation()
    })

    it('does not navigate when the confirmation dialog is cancelled', async () => {
      const user = userEvent.setup()
      stubFetch()
      const restoreLocation = stubLocation()
      renderSettingsPage(true)

      await user.click(await screen.findByRole('button', { name: 'Log off' }))
      await user.click(await screen.findByRole('button', { name: 'Cancel' }))

      expect(screen.queryByRole('heading', { name: 'Log off?' })).not.toBeInTheDocument()
      expect(window.location.href).toBe('')
      restoreLocation()
    })
  })
})
