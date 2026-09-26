import 'fake-indexeddb/auto'
import { act, renderHook, waitFor } from '@testing-library/react'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { IDBFactory } from 'fake-indexeddb'
import { enqueue } from '@/lib/offline-queue'
import { useLogoff } from './use-logoff'

function jsonResponse(body: object | null, status = 200) {
  return new Response(body === null ? null : JSON.stringify(body), { status })
}

const householdId = '11111111-1111-1111-1111-111111111111'

function stubLocation() {
  const originalLocation = window.location
  const mockLocation = { ...originalLocation, href: '' }
  Object.defineProperty(window, 'location', { value: mockLocation, writable: true })
  return () => Object.defineProperty(window, 'location', { value: originalLocation, writable: true })
}

describe('useLogoff', () => {
  beforeEach(() => {
    globalThis.indexedDB = new IDBFactory()
  })

  afterEach(() => {
    vi.unstubAllGlobals()
  })

  it('starts closed and opens to the confirm step', () => {
    vi.stubGlobal('fetch', vi.fn(() => Promise.resolve(jsonResponse(null))))
    const { result } = renderHook(() => useLogoff(householdId, true))

    expect(result.current.logoffStep).toBe('closed')

    act(() => result.current.openLogoffDialog())

    expect(result.current.logoffStep).toBe('confirm')
  })

  it('navigates straight to /logout when federated logout is supported and no readings are queued (AC #2, #6)', async () => {
    vi.stubGlobal('fetch', vi.fn(() => Promise.resolve(jsonResponse(null))))
    const restoreLocation = stubLocation()
    const { result } = renderHook(() => useLogoff(householdId, true))

    await act(() => result.current.handleConfirmLogoff())

    await waitFor(() => expect(window.location.href).toBe('/logout'))
    restoreLocation()
  })

  it('shows a warning instead of navigating when federated logout is not supported, and proceeds only after Continue (AC #3)', async () => {
    vi.stubGlobal('fetch', vi.fn(() => Promise.resolve(jsonResponse(null))))
    const restoreLocation = stubLocation()
    const { result } = renderHook(() => useLogoff(householdId, false))

    await act(() => result.current.handleConfirmLogoff())

    expect(result.current.logoffStep).toBe('federated-warning')
    expect(window.location.href).toBe('')

    // Mirrors the UI: the federated-warning step's own "Continue" button calls
    // navigateToLogout directly, not proceedPastQueueCheck again (settings-page.tsx).
    act(() => result.current.navigateToLogout())
    await waitFor(() => expect(window.location.href).toBe('/logout'))
    restoreLocation()
  })

  it('surfaces still-queued readings instead of silently discarding them, and only navigates after an explicit choice (AC #4)', async () => {
    vi.stubGlobal('fetch', vi.fn(() => Promise.resolve(jsonResponse({ detail: 'unreachable' }, 503))))
    await enqueue({ householdId, kwhValue: 4821.5, readingTimestamp: '2026-08-15T14:32:00Z', idempotencyKey: 'key-1' })
    const restoreLocation = stubLocation()
    const { result } = renderHook(() => useLogoff(householdId, true))

    await act(() => result.current.handleConfirmLogoff())

    expect(result.current.logoffStep).toBe('queue-warning')
    expect(result.current.pendingReadingCount).toBe(1)
    expect(window.location.href).toBe('')

    act(() => result.current.proceedPastQueueCheck())
    await waitFor(() => expect(window.location.href).toBe('/logout'))
    restoreLocation()
  })

  it('closes back to the resting state', () => {
    vi.stubGlobal('fetch', vi.fn(() => Promise.resolve(jsonResponse(null))))
    const { result } = renderHook(() => useLogoff(householdId, true))

    act(() => result.current.openLogoffDialog())
    act(() => result.current.closeLogoffDialog())

    expect(result.current.logoffStep).toBe('closed')
  })
})
