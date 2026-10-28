import 'fake-indexeddb/auto'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { IDBFactory } from 'fake-indexeddb'
import { enqueue, listPending, markRejected } from './offline-queue'
import { checkPendingReadingsBeforeLogoff } from './logoff'

const householdId = '11111111-1111-1111-1111-111111111111'
const otherHouseholdId = '22222222-2222-2222-2222-222222222222'
const reading = { householdId, kwhValue: 4821.5, readingTimestamp: '2026-08-15T14:32:00Z', idempotencyKey: 'key-1' }

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

    const result = await checkPendingReadingsBeforeLogoff(householdId)

    expect(result.pendingCount).toBe(0)
    expect(await listPending()).toEqual([])
    expect(fetchMock).toHaveBeenCalledOnce()
  })

  it('does not attempt a network flush when offline, and reports the still-queued reading', async () => {
    await enqueue(reading)
    stubOnline(false)
    const fetchMock = vi.fn()
    vi.stubGlobal('fetch', fetchMock)

    const result = await checkPendingReadingsBeforeLogoff(householdId)

    expect(result.pendingCount).toBe(1)
    expect(fetchMock).not.toHaveBeenCalled()
  })

  it('reports the reading as still pending when online but the flush attempt fails', async () => {
    await enqueue(reading)
    stubOnline(true)
    vi.stubGlobal('fetch', vi.fn(() => Promise.reject(new Error('network down'))))

    const result = await checkPendingReadingsBeforeLogoff(householdId)

    expect(result.pendingCount).toBe(1)
    expect(await listPending()).toEqual([reading])
  })

  it('reports zero pending when the queue is already empty', async () => {
    stubOnline(true)
    vi.stubGlobal('fetch', vi.fn())

    const result = await checkPendingReadingsBeforeLogoff(householdId)

    expect(result.pendingCount).toBe(0)
  })

  it('AC #4: never flushes or counts a reading queued by a different Household — leaves it queued untouched', async () => {
    const otherHouseholdReading = { ...reading, householdId: otherHouseholdId, idempotencyKey: 'key-2' }
    await enqueue(otherHouseholdReading)
    stubOnline(true)
    const fetchMock = vi.fn()
    vi.stubGlobal('fetch', fetchMock)

    const result = await checkPendingReadingsBeforeLogoff(householdId)

    expect(result.pendingCount).toBe(0)
    expect(fetchMock).not.toHaveBeenCalled()
    expect(await listPending()).toEqual([otherHouseholdReading])
  })
})

// Story 11.16 (AC #11): rejected entries are still on the device, so they are counted; attentionCount says how many.
describe('checkPendingReadingsBeforeLogoff: readings the server rejected (Story 11.16)', () => {
  it('counts a rejected entry in pendingCount and in attentionCount, a pending one only in pendingCount', async () => {
    await enqueue({ ...reading, idempotencyKey: 'rejected' })
    await markRejected('rejected', { status: 400, detail: 'too far in the future' })
    await enqueue({ ...reading, idempotencyKey: 'pending' })
    stubOnline(false)
    vi.stubGlobal('fetch', vi.fn())

    const result = await checkPendingReadingsBeforeLogoff(householdId)

    expect(result).toEqual({ pendingCount: 2, attentionCount: 1 })
  })

  it("counts another Household's rejected entry in neither", async () => {
    await enqueue({ ...reading, householdId: otherHouseholdId, idempotencyKey: 'theirs' })
    await markRejected('theirs', { status: 400, detail: null })
    stubOnline(false)
    vi.stubGlobal('fetch', vi.fn())

    const result = await checkPendingReadingsBeforeLogoff(householdId)

    expect(result).toEqual({ pendingCount: 0, attentionCount: 0 })
  })

  it('counts an entry the online flush newly rejects', async () => {
    await enqueue(reading)
    stubOnline(true)
    vi.stubGlobal('fetch', vi.fn(() => Promise.resolve(jsonResponse({ detail: 'too far in the future' }, 400))))

    const result = await checkPendingReadingsBeforeLogoff(householdId)

    expect(result).toEqual({ pendingCount: 1, attentionCount: 1 })
  })

  it('does not re-post an already rejected entry during the online flush', async () => {
    await enqueue(reading)
    await markRejected(reading.idempotencyKey, { status: 400, detail: null })
    stubOnline(true)
    const fetchMock = vi.fn()
    vi.stubGlobal('fetch', fetchMock)

    const result = await checkPendingReadingsBeforeLogoff(householdId)

    expect(fetchMock).not.toHaveBeenCalled()
    expect(result).toEqual({ pendingCount: 1, attentionCount: 1 })
  })
})

