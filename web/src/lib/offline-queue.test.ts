import 'fake-indexeddb/auto'
import { beforeEach, describe, expect, it } from 'vitest'
import { enqueue, listPending, remove, removeForHousehold } from './offline-queue'
import { IDBFactory } from 'fake-indexeddb'

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
