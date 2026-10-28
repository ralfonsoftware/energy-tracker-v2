import 'fake-indexeddb/auto'
import { act, renderHook, waitFor } from '@testing-library/react'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { IDBFactory } from 'fake-indexeddb'
import { enqueue, markRejected } from '@/lib/offline-queue'
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

// Story 11.8 (AC #3, #5, #10): POST /logout through the shared client; the SPA navigates to the URL the
// server returns. `flush` answers anything that is not /logout (the offline queue's flush).
function stubFetch(logout: () => Promise<Response>, flush: () => Response = () => jsonResponse(null)) {
  const fetchMock = vi.fn((url: string) => (url === '/logout' ? logout() : Promise.resolve(flush())))
  vi.stubGlobal('fetch', fetchMock)
  return fetchMock
}

function logoutCalls(fetchMock: ReturnType<typeof stubFetch>) {
  return fetchMock.mock.calls.filter(([url]) => url === '/logout') as unknown as [string, RequestInit][]
}

describe('useLogoff', () => {
  let restoreLocation: () => void

  beforeEach(() => {
    globalThis.indexedDB = new IDBFactory()
    restoreLocation = stubLocation()
  })

  afterEach(() => {
    restoreLocation()
    vi.unstubAllGlobals()
  })

  it('starts closed and opens to the confirm step', () => {
    stubFetch(() => Promise.resolve(jsonResponse({ redirectUrl: '/' })))
    const { result } = renderHook(() => useLogoff(householdId, true))

    expect(result.current.logoffStep).toBe('closed')
    expect(result.current.logoffError).toBe(false)

    act(() => result.current.openLogoffDialog())

    expect(result.current.logoffStep).toBe('confirm')
  })

  it('POSTs /logout with the CSRF header and navigates to the returned URL when federated logout is supported and nothing is queued (AC #3, #5)', async () => {
    const fetchMock = stubFetch(() => Promise.resolve(jsonResponse({ redirectUrl: 'https://tenant.example/oidc/logout?client_id=abc&state=xyz' })))
    const { result } = renderHook(() => useLogoff(householdId, true))

    await act(() => result.current.handleConfirmLogoff())

    const [call] = logoutCalls(fetchMock)
    expect(logoutCalls(fetchMock)).toHaveLength(1)
    expect(call[1].method).toBe('POST')
    expect(new Headers(call[1].headers).get('X-Requested-With')).toBe('fetch')
    expect(call[1].credentials).toBe('include')
    await waitFor(() => expect(window.location.href).toBe('https://tenant.example/oidc/logout?client_id=abc&state=xyz'))
  })

  it('navigates to / when the server has nothing to redirect to', async () => {
    stubFetch(() => Promise.resolve(jsonResponse({ redirectUrl: '/' })))
    const { result } = renderHook(() => useLogoff(householdId, true))

    await act(() => result.current.handleConfirmLogoff())

    expect(window.location.href).toBe('/')
  })

  it.each([
    ['a javascript: URL', 'javascript:alert(1)'],
    ['a protocol-relative URL', '//evil.example/x'],
    ['a data: URL', 'data:text/html,<script>alert(1)</script>'],
    ['a tab-smuggled protocol-relative URL', '/\t/evil.example'],
    ['a newline-smuggled protocol-relative URL', '/\n/evil.example'],
    ['a backslash URL', '/\\evil.example'],
    ['an empty string', ''],
    ['not a string', 42],
    ['a missing value', undefined],
  ])('replaces %s with / instead of navigating to it', async (_label, redirectUrl) => {
    stubFetch(() => Promise.resolve(jsonResponse({ redirectUrl } as object)))
    const { result } = renderHook(() => useLogoff(householdId, true))

    await act(() => result.current.handleConfirmLogoff())

    expect(window.location.href).toBe('/')
  })

  it('keeps a same-origin path with its query and hash', async () => {
    stubFetch(() => Promise.resolve(jsonResponse({ redirectUrl: '/signed-out?x=1#top' })))
    const { result } = renderHook(() => useLogoff(householdId, true))

    await act(() => result.current.handleConfirmLogoff())

    expect(window.location.href).toBe('/signed-out?x=1#top')
  })

  it('shows a warning instead of POSTing when federated logout is not supported, and POSTs only after Continue (AC #3)', async () => {
    const fetchMock = stubFetch(() => Promise.resolve(jsonResponse({ redirectUrl: '/' })))
    const { result } = renderHook(() => useLogoff(householdId, false))

    await act(() => result.current.handleConfirmLogoff())

    expect(result.current.logoffStep).toBe('federated-warning')
    expect(logoutCalls(fetchMock)).toHaveLength(0)
    expect(window.location.href).toBe('')

    // Mirrors the UI: the federated-warning step's own "Continue" button calls navigateToLogout
    // directly, not proceedPastQueueCheck again.
    await act(() => result.current.navigateToLogout())

    expect(logoutCalls(fetchMock)).toHaveLength(1)
    await waitFor(() => expect(window.location.href).toBe('/'))
  })

  it('surfaces still-queued readings instead of silently discarding them, and only POSTs after an explicit choice (AC #3)', async () => {
    const fetchMock = stubFetch(
      () => Promise.resolve(jsonResponse({ redirectUrl: '/' })),
      () => jsonResponse({ detail: 'unreachable' }, 503),
    )
    await enqueue({ householdId, kwhValue: 4821.5, readingTimestamp: '2026-08-15T14:32:00Z', idempotencyKey: 'key-1' })
    const { result } = renderHook(() => useLogoff(householdId, true))

    await act(() => result.current.handleConfirmLogoff())

    expect(result.current.logoffStep).toBe('queue-warning')
    expect(result.current.pendingReadingCount).toBe(1)
    expect(logoutCalls(fetchMock)).toHaveLength(0)
    expect(window.location.href).toBe('')

    await act(() => result.current.proceedPastQueueCheck())
    await waitFor(() => expect(window.location.href).toBe('/'))
    expect(logoutCalls(fetchMock)).toHaveLength(1)
  })

  // Story 11.16 (AC #11): rejected readings are still on the device, so they count, and the dialog is told how many.
  it('counts a server-rejected reading in pendingReadingCount and attentionReadingCount', async () => {
    stubFetch(
      () => Promise.resolve(jsonResponse({ redirectUrl: '/' })),
      () => jsonResponse({ detail: 'unreachable' }, 503),
    )
    await enqueue({ householdId, kwhValue: 4821.5, readingTimestamp: '2026-08-15T14:32:00Z', idempotencyKey: 'rejected' })
    await markRejected('rejected', { status: 400, detail: 'too far in the future' })
    await enqueue({ householdId, kwhValue: 4830, readingTimestamp: '2026-08-16T14:32:00Z', idempotencyKey: 'pending' })
    const { result } = renderHook(() => useLogoff(householdId, true))

    await act(() => result.current.handleConfirmLogoff())

    expect(result.current.logoffStep).toBe('queue-warning')
    expect(result.current.pendingReadingCount).toBe(2)
    expect(result.current.attentionReadingCount).toBe(1)
  })

  it('reports no attention readings when everything queued is merely pending', async () => {
    stubFetch(
      () => Promise.resolve(jsonResponse({ redirectUrl: '/' })),
      () => jsonResponse({ detail: 'unreachable' }, 503),
    )
    await enqueue({ householdId, kwhValue: 4821.5, readingTimestamp: '2026-08-15T14:32:00Z', idempotencyKey: 'key-1' })
    const { result } = renderHook(() => useLogoff(householdId, true))

    await act(() => result.current.handleConfirmLogoff())

    expect(result.current.pendingReadingCount).toBe(1)
    expect(result.current.attentionReadingCount).toBe(0)
  })

  it('keeps the member on the confirm step with an error when the POST is rejected (AC #10)', async () => {
    stubFetch(() => Promise.reject(new TypeError('Failed to fetch')))
    const { result } = renderHook(() => useLogoff(householdId, true))
    act(() => result.current.openLogoffDialog())

    await act(() => result.current.handleConfirmLogoff())

    expect(window.location.href).toBe('')
    expect(result.current.logoffError).toBe(true)
    expect(result.current.logoffStep).toBe('confirm')
    expect(result.current.logoffChecking).toBe(false)
  })

  it('treats a 500 like a failure and lets the member retry (AC #10)', async () => {
    let healthy = false
    stubFetch(() => Promise.resolve(healthy ? jsonResponse({ redirectUrl: '/' }) : jsonResponse({ detail: 'boom' }, 500)))
    const { result } = renderHook(() => useLogoff(householdId, true))
    act(() => result.current.openLogoffDialog())

    await act(() => result.current.handleConfirmLogoff())

    expect(result.current.logoffError).toBe(true)
    expect(window.location.href).toBe('')

    healthy = true
    await act(() => result.current.handleConfirmLogoff())

    expect(result.current.logoffError).toBe(false)
    expect(window.location.href).toBe('/')
  })

  it('treats a non-JSON 200 as a failure (AC #10)', async () => {
    stubFetch(() => Promise.resolve(new Response('<html>proxy error</html>', { status: 200 })))
    const { result } = renderHook(() => useLogoff(householdId, true))

    await act(() => result.current.handleConfirmLogoff())

    expect(result.current.logoffError).toBe(true)
    expect(window.location.href).toBe('')
  })

  it('stays busy for the whole request so the buttons cannot start a second POST (AC #10)', async () => {
    let finish: (response: Response) => void = () => {}
    const pending = new Promise<Response>((resolve) => {
      finish = resolve
    })
    const fetchMock = stubFetch(() => pending)
    const { result } = renderHook(() => useLogoff(householdId, true))

    let confirming: Promise<void> = Promise.resolve()
    act(() => {
      confirming = result.current.handleConfirmLogoff()
    })

    await waitFor(() => expect(logoutCalls(fetchMock)).toHaveLength(1))
    expect(result.current.logoffChecking).toBe(true)

    await act(async () => {
      finish(jsonResponse({ redirectUrl: '/' }))
      await confirming
    })

    // Still busy: the browser is leaving the page, so no button may start a second request.
    expect(result.current.logoffChecking).toBe(true)
    expect(window.location.href).toBe('/')
  })

  it('clears an earlier error when a new attempt starts', async () => {
    let ok = false
    stubFetch(() => Promise.resolve(ok ? jsonResponse({ redirectUrl: '/' }) : jsonResponse(null, 503)))
    const { result } = renderHook(() => useLogoff(householdId, true))

    await act(() => result.current.navigateToLogout())
    expect(result.current.logoffError).toBe(true)

    ok = true
    await act(() => result.current.navigateToLogout())
    expect(result.current.logoffError).toBe(false)
  })

  it('becomes usable again when the browser restores the page from the back/forward cache after a successful POST', async () => {
    stubFetch(() => Promise.resolve(jsonResponse({ redirectUrl: 'https://tenant.example/oidc/logout?state=abc' })))
    const { result } = renderHook(() => useLogoff(householdId, true))
    act(() => result.current.openLogoffDialog())
    await act(() => result.current.handleConfirmLogoff())
    expect(result.current.logoffChecking).toBe(true)

    // A normal pageshow (not from the cache) must not reset it: the browser is still on its way out.
    act(() => {
      window.dispatchEvent(Object.assign(new Event('pageshow'), { persisted: false }))
    })
    expect(result.current.logoffChecking).toBe(true)

    act(() => {
      window.dispatchEvent(Object.assign(new Event('pageshow'), { persisted: true }))
    })
    expect(result.current.logoffChecking).toBe(false)
  })

  it('closes back to the resting state', () => {
    stubFetch(() => Promise.resolve(jsonResponse({ redirectUrl: '/' })))
    const { result } = renderHook(() => useLogoff(householdId, true))

    act(() => result.current.openLogoffDialog())
    act(() => result.current.closeLogoffDialog())

    expect(result.current.logoffStep).toBe('closed')
  })

  it('forgets an old error when the dialog is closed and reopened', async () => {
    stubFetch(() => Promise.reject(new TypeError('Failed to fetch')))
    const { result } = renderHook(() => useLogoff(householdId, true))
    act(() => result.current.openLogoffDialog())
    await act(() => result.current.handleConfirmLogoff())
    expect(result.current.logoffError).toBe(true)

    act(() => result.current.closeLogoffDialog())
    act(() => result.current.openLogoffDialog())

    expect(result.current.logoffError).toBe(false)
  })
})
