import 'fake-indexeddb/auto'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import {
  enqueue,
  isNeedsAttention,
  listNeedsAttention,
  listPending,
  markRejected,
  QUEUE_CHANGED_EVENT,
  remove,
  removeForHousehold,
  resubmit,
} from './offline-queue'
import { IDBDatabase, IDBFactory } from 'fake-indexeddb'

beforeEach(() => {
  // jsdom has no native IndexedDB implementation — fake-indexeddb/auto stubs `indexedDB` globally.
  // Reset it between tests so each test starts from an empty database.
  globalThis.indexedDB = new IDBFactory()
})

describe('offline-queue', () => {
  const householdId = '11111111-1111-1111-1111-111111111111'

  it('enqueues a reading and lists it back via listPending', async () => {
    await enqueue({ householdId, kwhValue: 4821.5, readingTimestamp: '2026-08-15T14:32:00Z', idempotencyKey: 'key-1' })

    const pending = await listPending()

    expect(pending).toEqual([{ householdId, kwhValue: 4821.5, readingTimestamp: '2026-08-15T14:32:00Z', idempotencyKey: 'key-1' }])
  })

  it('lists multiple queued readings', async () => {
    await enqueue({ householdId, kwhValue: 100, readingTimestamp: '2026-08-15T08:00:00Z', idempotencyKey: 'key-1' })
    await enqueue({ householdId, kwhValue: 105, readingTimestamp: '2026-08-15T12:00:00Z', idempotencyKey: 'key-2' })

    const pending = await listPending()

    expect(pending).toHaveLength(2)
    expect(pending.map((r) => r.idempotencyKey).sort()).toEqual(['key-1', 'key-2'])
  })

  it('removes a queued reading by idempotencyKey', async () => {
    await enqueue({ householdId, kwhValue: 4821.5, readingTimestamp: '2026-08-15T14:32:00Z', idempotencyKey: 'key-1' })

    await remove('key-1')

    expect(await listPending()).toEqual([])
  })

  it('removing a key that is not queued is a no-op', async () => {
    await enqueue({ householdId, kwhValue: 4821.5, readingTimestamp: '2026-08-15T14:32:00Z', idempotencyKey: 'key-1' })

    await remove('nonexistent-key')

    expect(await listPending()).toHaveLength(1)
  })

  // Story 11.9 (AC #13b, amended): leaving a Household discards that Household's unsynced readings only.
  it('removeForHousehold deletes only the entries of that Household', async () => {
    const other = '22222222-2222-2222-2222-222222222222'
    await enqueue({ householdId, kwhValue: 100, readingTimestamp: '2026-08-15T08:00:00Z', idempotencyKey: 'key-1' })
    await enqueue({ householdId, kwhValue: 105, readingTimestamp: '2026-08-15T12:00:00Z', idempotencyKey: 'key-2' })
    await enqueue({ householdId: other, kwhValue: 7, readingTimestamp: '2026-08-15T12:00:00Z', idempotencyKey: 'key-3' })

    await removeForHousehold(householdId)

    expect((await listPending()).map((r) => r.idempotencyKey)).toEqual(['key-3'])
  })

  it('removeForHousehold is a no-op when the Household has no queued entries', async () => {
    await enqueue({ householdId, kwhValue: 100, readingTimestamp: '2026-08-15T08:00:00Z', idempotencyKey: 'key-1' })

    await removeForHousehold('33333333-3333-3333-3333-333333333333')

    expect(await listPending()).toHaveLength(1)
  })
})

// Story 11.16 (audit C4): a reading the server refused stays in the store, marked `rejection`.
describe('offline-queue: needs attention (Story 11.16)', () => {
  const householdId = '11111111-1111-1111-1111-111111111111'
  const other = '22222222-2222-2222-2222-222222222222'
  const base = { householdId, kwhValue: 100, readingTimestamp: '2026-08-15T08:00:00Z', idempotencyKey: 'key-1' }

  function countChangeEvents(): { count: () => number; stop: () => void } {
    let n = 0
    const listener = () => {
      n += 1
    }
    window.addEventListener(QUEUE_CHANGED_EVENT, listener)
    return { count: () => n, stop: () => window.removeEventListener(QUEUE_CHANGED_EVENT, listener) }
  }

  it('markRejected keeps the entry, adds rejection and leaves every other field untouched', async () => {
    await enqueue(base)

    await markRejected('key-1', { status: 400, detail: 'Reading timestamp is too far in the future.' })

    const [entry] = await listPending()
    expect(entry).toMatchObject(base)
    expect(entry.rejection).toEqual({
      status: 400,
      detail: 'Reading timestamp is too far in the future.',
      rejectedAtUtc: expect.stringMatching(/^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{3}Z$/),
    })
  })

  it('markRejected stores a null detail as null', async () => {
    await enqueue(base)

    await markRejected('key-1', { status: 400, detail: null })

    expect((await listPending())[0].rejection?.detail).toBeNull()
  })

  it('markRejected on a key that is not in the store does nothing and does not create it', async () => {
    await markRejected('ghost', { status: 400, detail: 'x' })

    expect(await listPending()).toEqual([])
  })

  it('listNeedsAttention returns only that Household\'s rejected entries, oldest reading first', async () => {
    await enqueue({ ...base, idempotencyKey: 'late', readingTimestamp: '2026-08-15T12:00:00Z' })
    await enqueue({ ...base, idempotencyKey: 'early', readingTimestamp: '2026-08-15T06:00:00Z' })
    await enqueue({ ...base, idempotencyKey: 'pending' })
    await enqueue({ ...base, idempotencyKey: 'theirs', householdId: other })
    await markRejected('late', { status: 400, detail: null })
    await markRejected('early', { status: 400, detail: null })
    await markRejected('theirs', { status: 400, detail: null })

    const rejected = await listNeedsAttention(householdId)

    expect(rejected.map((r) => r.idempotencyKey)).toEqual(['early', 'late'])
  })

  it('listPending still returns pending and rejected entries (logoff and leave count both)', async () => {
    await enqueue(base)
    await enqueue({ ...base, idempotencyKey: 'key-2' })
    await markRejected('key-2', { status: 400, detail: null })

    expect((await listPending()).map((r) => r.idempotencyKey).sort()).toEqual(['key-1', 'key-2'])
  })

  it('removeForHousehold deletes rejected entries of that Household too, and only those', async () => {
    await enqueue(base)
    await enqueue({ ...base, idempotencyKey: 'key-2' })
    await enqueue({ ...base, idempotencyKey: 'key-3', householdId: other })
    await markRejected('key-2', { status: 400, detail: null })
    await markRejected('key-3', { status: 400, detail: null })

    await removeForHousehold(householdId)

    expect((await listPending()).map((r) => r.idempotencyKey)).toEqual(['key-3'])
  })

  it('resubmit rewrites both fields, clears rejection and keeps the key', async () => {
    await enqueue(base)
    await markRejected('key-1', { status: 400, detail: 'nope' })

    await resubmit('key-1', { kwhValue: 123.4, readingTimestamp: '2026-08-15T09:30:00.000Z' })

    const pending = await listPending()
    expect(pending).toEqual([
      { householdId, idempotencyKey: 'key-1', kwhValue: 123.4, readingTimestamp: '2026-08-15T09:30:00.000Z' },
    ])
    expect('rejection' in pending[0]).toBe(false)
  })

  it('resubmit on a missing key does nothing and does not create it', async () => {
    await resubmit('ghost', { kwhValue: 1, readingTimestamp: '2026-08-15T09:30:00.000Z' })

    expect(await listPending()).toEqual([])
  })

  it('every mutator dispatches QUEUE_CHANGED_EVENT exactly once, after the write is durable', async () => {
    const events = countChangeEvents()
    try {
      await enqueue(base)
      expect(events.count()).toBe(1)
      await markRejected('key-1', { status: 400, detail: null })
      expect(events.count()).toBe(2)
      await resubmit('key-1', { kwhValue: 5, readingTimestamp: '2026-08-15T09:30:00.000Z' })
      expect(events.count()).toBe(3)
      await remove('key-1')
      expect(events.count()).toBe(4)
    } finally {
      events.stop()
    }
  })

  // IndexedDB orders a later read after an earlier write, so reading from the listener cannot tell an early
  // dispatch from a late one; record the order of the transaction's own `complete` and the event instead.
  it.each([
    ['enqueue', async () => enqueue(base)],
    ['markRejected', async () => { await enqueue(base); await markRejected('key-1', { status: 400, detail: null }) }],
    ['resubmit', async () => { await enqueue(base); await resubmit('key-1', { kwhValue: 5, readingTimestamp: '2026-08-15T09:30:00.000Z' }) }],
    ['remove', async () => { await enqueue(base); await remove('key-1') }],
  ])('%s dispatches only after its write transaction has completed', async (_name, act) => {
    const realTransaction = IDBDatabase.prototype.transaction
    const spy = vi.spyOn(IDBDatabase.prototype, 'transaction').mockImplementation(function (this: IDBDatabase, ...args: Parameters<IDBDatabase['transaction']>) {
      const transaction = realTransaction.apply(this, args)
      if (args[1] === 'readwrite') {
        transaction.addEventListener('complete', () => order.push('complete'))
      }
      return transaction
    })
    const order: string[] = []
    const listener = () => order.push('event')
    window.addEventListener(QUEUE_CHANGED_EVENT, listener)
    try {
      await act()
    } finally {
      window.removeEventListener(QUEUE_CHANGED_EVENT, listener)
      spy.mockRestore()
    }

    // Every dispatch is immediately preceded by the completion of the transaction that caused it.
    expect(order.filter((entry) => entry === 'event').length).toBeGreaterThan(0)
    order.forEach((entry, index) => {
      if (entry === 'event') {
        expect(order[index - 1]).toBe('complete')
      }
    })
  })

  it('a no-op (missing key) dispatches nothing', async () => {
    const events = countChangeEvents()
    try {
      await markRejected('ghost', { status: 400, detail: null })
      await resubmit('ghost', { kwhValue: 1, readingTimestamp: '2026-08-15T09:30:00.000Z' })
      expect(events.count()).toBe(0)
    } finally {
      events.stop()
    }
  })

  it('a legacy entry without rejection is pending, not needs attention', async () => {
    await enqueue(base)

    const [entry] = await listPending()

    expect(isNeedsAttention(entry)).toBe(false)
    expect(await listNeedsAttention(householdId)).toEqual([])
  })

  it('isNeedsAttention is true for an entry with rejection', async () => {
    await enqueue(base)
    await markRejected('key-1', { status: 400, detail: null })

    expect(isNeedsAttention((await listPending())[0])).toBe(true)
  })

  // Story 11.16 code review: a caller has to be able to tell "changed" from "entry was gone".
  it('markRejected and resubmit report whether they changed an entry', async () => {
    await enqueue(base)

    expect(await markRejected('key-1', { status: 400, detail: null })).toBe(true)
    expect(await resubmit('key-1', { kwhValue: 1, readingTimestamp: '2026-08-15T09:30:00.000Z' })).toBe(true)
    expect(await markRejected('ghost', { status: 400, detail: null })).toBe(false)
    expect(await resubmit('ghost', { kwhValue: 1, readingTimestamp: '2026-08-15T09:30:00.000Z' })).toBe(false)
  })

  // Story 11.16 code review: a transaction that aborts without a request error (quota, commit failure,
  // an exception inside a handler) must reject, or the flush guard and the resend dialog hang forever.
  it('rejects instead of hanging when the write transaction aborts', async () => {
    await enqueue(base)
    const uncloneable = (() => {}) as unknown as string

    await expect(markRejected('key-1', { status: 400, detail: uncloneable })).rejects.toBeDefined()
    // the aborted write left the entry as it was
    expect(isNeedsAttention((await listPending())[0])).toBe(false)
  })
})
