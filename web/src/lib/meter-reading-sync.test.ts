import 'fake-indexeddb/auto'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { IDBFactory } from 'fake-indexeddb'
import { enqueue, isNeedsAttention, listPending, markRejected, remove, type QueuedMeterReading } from './offline-queue'
import { flushQueue, resendRejectedReading } from './meter-reading-sync'

const householdId = '11111111-1111-1111-1111-111111111111'
const otherHouseholdId = '22222222-2222-2222-2222-222222222222'
const reading: QueuedMeterReading = { householdId, kwhValue: 4821.5, readingTimestamp: '2026-08-15T14:32:00Z', idempotencyKey: 'key-1' }

function jsonResponse(body: object, status = 200) {
  return new Response(JSON.stringify(body), { status })
}

const okBody = { id: 'r1', kwhValue: reading.kwhValue, readingTimestamp: reading.readingTimestamp }
const problem = (detail: string, status = 400) => jsonResponse({ title: 'Bad Request', status, detail }, status)

function stubFetch(handler: (init: RequestInit) => Promise<Response> | Response) {
  const fetchMock = vi.fn((_url: string, init: RequestInit) => Promise.resolve(handler(init)))
  vi.stubGlobal('fetch', fetchMock)
  return fetchMock
}

function sentBody(fetchMock: ReturnType<typeof stubFetch>, call = 0) {
  return JSON.parse(fetchMock.mock.calls[call][1].body as string) as Record<string, unknown>
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

// Story 11.16 (audit C4): a reading the server permanently refuses stays on the device, marked.
describe('flushQueue', () => {
  it('C4: keeps a reading the server rejected with a 400, marked with the server reason', async () => {
    await enqueue(reading)
    stubFetch(() => problem('Reading timestamp is too far in the future.'))
    const onSynced = vi.fn()

    await flushQueue(householdId, onSynced)

    const [entry] = await listPending()
    expect(entry).toMatchObject({ ...reading, rejection: { status: 400, detail: 'Reading timestamp is too far in the future.' } })
    expect(onSynced).not.toHaveBeenCalled()
  })

  it('stores a null detail when the rejection body is not JSON', async () => {
    await enqueue(reading)
    stubFetch(() => new Response('<html>Bad Request</html>', { status: 400 }))

    await flushQueue(householdId)

    expect((await listPending())[0].rejection).toMatchObject({ status: 400, detail: null })
  })

  it.each(['', '   '])('stores a null detail when the problem body has a blank detail (%j)', async (detail) => {
    await enqueue(reading)
    stubFetch(() => jsonResponse({ detail }, 400))

    await flushQueue(householdId)

    expect((await listPending())[0].rejection?.detail).toBeNull()
  })

  it('removes a reading the server accepted and calls onSynced once', async () => {
    await enqueue(reading)
    stubFetch(() => jsonResponse(okBody))
    const onSynced = vi.fn()

    await flushQueue(householdId, onSynced)

    expect(await listPending()).toEqual([])
    expect(onSynced).toHaveBeenCalledOnce()
  })

  it.each([401, 403, 408, 429, 500, 503])('leaves the entry untouched on a transient %i', async (status) => {
    await enqueue(reading)
    stubFetch(() => jsonResponse({ detail: 'try later' }, status))

    await flushQueue(householdId)

    expect(await listPending()).toEqual([reading])
  })

  it('leaves the entry untouched when fetch rejects (offline)', async () => {
    await enqueue(reading)
    stubFetch(() => Promise.reject(new Error('network down')))

    await flushQueue(householdId)

    expect(await listPending()).toEqual([reading])
  })

  it.each([404, 405, 413, 415, 422])('marks the entry needs-attention on a permanent %i', async (status) => {
    await enqueue(reading)
    stubFetch(() => jsonResponse({ detail: `refused ${status}` }, status))

    await flushQueue(householdId)

    const [entry] = await listPending()
    expect(isNeedsAttention(entry)).toBe(true)
    expect(entry.rejection).toMatchObject({ status, detail: `refused ${status}` })
  })

  it('does not POST an entry that is already needs-attention, but still posts a pending one next to it', async () => {
    await enqueue({ ...reading, idempotencyKey: 'rejected' })
    await markRejected('rejected', { status: 400, detail: 'no' })
    await enqueue({ ...reading, idempotencyKey: 'pending' })
    const fetchMock = stubFetch(() => jsonResponse(okBody))

    await flushQueue(householdId)

    expect(fetchMock).toHaveBeenCalledOnce()
    expect(sentBody(fetchMock).idempotencyKey).toBe('pending')
    expect((await listPending()).map((r) => r.idempotencyKey)).toEqual(['rejected'])
  })

  it("skips another Household's entry, pending or rejected", async () => {
    await enqueue({ ...reading, idempotencyKey: 'theirs-pending', householdId: otherHouseholdId })
    await enqueue({ ...reading, idempotencyKey: 'theirs-rejected', householdId: otherHouseholdId })
    await markRejected('theirs-rejected', { status: 400, detail: null })
    const fetchMock = stubFetch(() => jsonResponse(okBody))

    await flushQueue(householdId)

    expect(fetchMock).not.toHaveBeenCalled()
    expect(await listPending()).toHaveLength(2)
  })

  it('posts exactly kwhValue, readingTimestamp and idempotencyKey', async () => {
    await enqueue(reading)
    const fetchMock = stubFetch(() => jsonResponse(okBody))

    await flushQueue(householdId)

    expect(sentBody(fetchMock)).toEqual({
      kwhValue: reading.kwhValue,
      readingTimestamp: reading.readingTimestamp,
      idempotencyKey: reading.idempotencyKey,
    })
  })

  it('does not log the old "Discarding queued meter reading" message', async () => {
    await enqueue(reading)
    stubFetch(() => problem('nope'))
    const errorSpy = vi.spyOn(console, 'error').mockImplementation(() => {})

    await flushQueue(householdId)

    expect(errorSpy.mock.calls.flat().join(' ')).not.toContain('Discarding queued meter reading')
  })

  it('does not resurrect an entry that was removed while its POST was in flight', async () => {
    await enqueue(reading)
    const { remove } = await import('./offline-queue')
    stubFetch(async () => {
      await remove(reading.idempotencyKey)
      return problem('nope')
    })

    await flushQueue(householdId)

    expect(await listPending()).toEqual([])
  })
})

describe('resendRejectedReading', () => {
  const correction = { kwhValue: 4830.25, readingTimestamp: '2026-08-15T15:00:00.000Z' }

  async function seedRejected() {
    await enqueue(reading)
    await markRejected(reading.idempotencyKey, { status: 400, detail: 'old reason' })
  }

  it('AC #3: posts the corrected values under the stored idempotencyKey', async () => {
    await seedRejected()
    const fetchMock = stubFetch(() => jsonResponse(okBody))

    await resendRejectedReading(householdId, reading.idempotencyKey, correction)

    expect(sentBody(fetchMock)).toEqual({ ...correction, idempotencyKey: reading.idempotencyKey })
  })

  it('removes the entry and answers sent on a 200', async () => {
    await seedRejected()
    stubFetch(() => jsonResponse(okBody))

    const result = await resendRejectedReading(householdId, reading.idempotencyKey, correction)

    expect(result).toEqual({ outcome: 'sent' })
    expect(await listPending()).toEqual([])
  })

  it('puts the entry back to needs-attention with the new status and detail on a permanent 400', async () => {
    await seedRejected()
    stubFetch(() => problem('new reason', 400))

    const result = await resendRejectedReading(householdId, reading.idempotencyKey, correction)

    const [entry] = await listPending()
    expect(entry).toMatchObject({ ...correction, idempotencyKey: reading.idempotencyKey })
    expect(entry.rejection).toMatchObject({ status: 400, detail: 'new reason' })
    expect(result).toEqual({ outcome: 'rejected', rejection: entry.rejection })
  })

  it('keeps the entry pending, with the corrected values stored, when the request gets no answer', async () => {
    await seedRejected()
    stubFetch(() => Promise.reject(new Error('down')))

    const result = await resendRejectedReading(householdId, reading.idempotencyKey, correction)

    expect(result).toEqual({ outcome: 'queued' })
    const [entry] = await listPending()
    expect(entry).toEqual({ householdId, idempotencyKey: reading.idempotencyKey, ...correction })
  })

  // Code review decision D1 (Ralf 2026-10-28, option b, AC #8 AMENDED): an HTTP answer that is
  // transient is not "offline", so the caller must be told and can show it.
  it.each([401, 403, 408, 429, 500, 503])(
    'answers unavailable with the status on a transient %i; the entry stays pending with the corrected values',
    async (status) => {
      await seedRejected()
      stubFetch(() => jsonResponse({ detail: 'try later' }, status))

      const result = await resendRejectedReading(householdId, reading.idempotencyKey, correction)

      expect(result).toEqual({ outcome: 'unavailable', status })
      const [entry] = await listPending()
      expect(entry).toEqual({ householdId, idempotencyKey: reading.idempotencyKey, ...correction })
    },
  )

  it('does not POST when the entry was removed after it was read (discarded elsewhere)', async () => {
    await seedRejected()
    const fetchMock = stubFetch(() => jsonResponse(okBody))

    const resend = resendRejectedReading(householdId, reading.idempotencyKey, correction)
    await remove(reading.idempotencyKey)
    const result = await resend

    expect(result).toEqual({ outcome: 'missing' })
    expect(fetchMock).not.toHaveBeenCalled()
    expect(await listPending()).toEqual([])
  })

  it('does not report a storage failure after the server answered as "saved offline"', async () => {
    await seedRejected()
    stubFetch(() => {
      // the server accepts, then the device store becomes unreadable before remove() runs
      vi.spyOn(globalThis.indexedDB, 'open').mockImplementation(() => {
        throw new Error('blocked storage')
      })
      return jsonResponse(okBody)
    })

    await expect(resendRejectedReading(householdId, reading.idempotencyKey, correction)).rejects.toThrow('blocked storage')
  })

  it('answers missing, without a request, for an unknown key', async () => {
    const fetchMock = stubFetch(() => jsonResponse(okBody))

    const result = await resendRejectedReading(householdId, 'ghost', correction)

    expect(result).toEqual({ outcome: 'missing' })
    expect(fetchMock).not.toHaveBeenCalled()
  })

  it("answers missing, without a request, for another Household's entry", async () => {
    await enqueue({ ...reading, householdId: otherHouseholdId })
    const fetchMock = stubFetch(() => jsonResponse(okBody))

    const result = await resendRejectedReading(householdId, reading.idempotencyKey, correction)

    expect(result).toEqual({ outcome: 'missing' })
    expect(fetchMock).not.toHaveBeenCalled()
    expect((await listPending())[0]).toEqual({ ...reading, householdId: otherHouseholdId })
  })

  it('has no way to pass a new idempotency key', async () => {
    await seedRejected()

    // The signature is the guard: a correction object cannot carry the key, so identity cannot change.
    const fetchMock = stubFetch(() => jsonResponse(okBody))
    await resendRejectedReading(householdId, reading.idempotencyKey, {
      ...correction,
      // @ts-expect-error idempotencyKey is not part of a correction (AD-16: the key is the identity)
      idempotencyKey: 'a-new-key',
    })

    // what reaches the server is the stored key, whatever the object carried at runtime
    expect(sentBody(fetchMock).idempotencyKey).toBe(reading.idempotencyKey)
    expect(await listPending()).toEqual([])
  })

  it('a resend racing a flushQueue over the same key ends with the entry removed (AD-16)', async () => {
    await seedRejected()
    const fetchMock = stubFetch(() => jsonResponse(okBody))

    // flushQueue skips needs-attention entries, so make the entry pending for the race first.
    const { resubmit } = await import('./offline-queue')
    await resubmit(reading.idempotencyKey, correction)
    await Promise.all([flushQueue(householdId), resendRejectedReading(householdId, reading.idempotencyKey, correction)])

    expect(await listPending()).toEqual([])
    for (const [, init] of fetchMock.mock.calls) {
      expect(JSON.parse(init.body as string).idempotencyKey).toBe(reading.idempotencyKey)
    }
  })
})
