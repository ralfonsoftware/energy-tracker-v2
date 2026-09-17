import 'fake-indexeddb/auto'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { IDBFactory } from 'fake-indexeddb'
import { enqueue, listPending } from './offline-queue'
import { checkPendingReadingsBeforeLogoff } from './logoff'

const reading = { kwhValue: 4821.5, readingTimestamp: '2026-08-15T14:32:00Z', idempotencyKey: 'key-1' }

function jsonResponse(body: object, status = 200) {
  return new Response(JSON.stringify(body), { status })
}

function stubOnline(online: boolean) {
  vi.spyOn(navigator, 'onLine', 'get').mockReturnValue(online)
}

beforeEach(() => {
  // jsdom has no native IndexedDB implementation — fake-indexeddb/auto stubs `indexedDB` globally.
  // Reset it between tests so each test starts from an empty database.
  globalThis.indexedDB = new IDBFactory()
})

afterEach(() => {
  vi.unstubAllGlobals()
  vi.restoreAllMocks()
})

describe('checkPendingReadingsBeforeLogoff', () => {
  it('flushes queued readings and reports zero pending when online and the flush succeeds', async () => {
    await enqueue(reading)
    stubOnline(true)
    const fetchMock = vi.fn(() => Promise.resolve(jsonResponse({ id: 'r1', kwhValue: reading.kwhValue, readingTimestamp: reading.readingTimestamp })))
    vi.stubGlobal('fetch', fetchMock)

    const result = await checkPendingReadingsBeforeLogoff()

    expect(result.pendingCount).toBe(0)
    expect(await listPending()).toEqual([])
    expect(fetchMock).toHaveBeenCalledOnce()
  })

  it('does not attempt a network flush when offline, and reports the still-queued reading', async () => {
    await enqueue(reading)
    stubOnline(false)
    const fetchMock = vi.fn()
    vi.stubGlobal('fetch', fetchMock)

    const result = await checkPendingReadingsBeforeLogoff()

    expect(result.pendingCount).toBe(1)
    expect(fetchMock).not.toHaveBeenCalled()
  })

  it('reports the reading as still pending when online but the flush attempt fails', async () => {
    await enqueue(reading)
    stubOnline(true)
    vi.stubGlobal('fetch', vi.fn(() => Promise.reject(new Error('network down'))))

    const result = await checkPendingReadingsBeforeLogoff()

    expect(result.pendingCount).toBe(1)
    expect(await listPending()).toEqual([reading])
  })

  it('reports zero pending when the queue is already empty', async () => {
    stubOnline(true)
    vi.stubGlobal('fetch', vi.fn())

    const result = await checkPendingReadingsBeforeLogoff()

    expect(result.pendingCount).toBe(0)
  })
})
