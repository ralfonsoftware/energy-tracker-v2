import 'fake-indexeddb/auto'
import { beforeEach, describe, expect, it } from 'vitest'
import { enqueue, listPending, remove } from './offline-queue'
import { IDBFactory } from 'fake-indexeddb'

beforeEach(() => {
  // jsdom has no native IndexedDB implementation — fake-indexeddb/auto stubs `indexedDB` globally.
  // Reset it between tests so each test starts from an empty database.
  globalThis.indexedDB = new IDBFactory()
})

describe('offline-queue', () => {
  it('enqueues a reading and lists it back via listPending', async () => {
    await enqueue({ kwhValue: 4821.5, readingTimestamp: '2026-08-15T14:32:00Z', idempotencyKey: 'key-1' })

    const pending = await listPending()

    expect(pending).toEqual([{ kwhValue: 4821.5, readingTimestamp: '2026-08-15T14:32:00Z', idempotencyKey: 'key-1' }])
  })

  it('lists multiple queued readings', async () => {
    await enqueue({ kwhValue: 100, readingTimestamp: '2026-08-15T08:00:00Z', idempotencyKey: 'key-1' })
    await enqueue({ kwhValue: 105, readingTimestamp: '2026-08-15T12:00:00Z', idempotencyKey: 'key-2' })

    const pending = await listPending()

    expect(pending).toHaveLength(2)
    expect(pending.map((r) => r.idempotencyKey).sort()).toEqual(['key-1', 'key-2'])
  })

  it('removes a queued reading by idempotencyKey', async () => {
    await enqueue({ kwhValue: 4821.5, readingTimestamp: '2026-08-15T14:32:00Z', idempotencyKey: 'key-1' })

    await remove('key-1')

    expect(await listPending()).toEqual([])
  })

  it('removing a key that is not queued is a no-op', async () => {
    await enqueue({ kwhValue: 4821.5, readingTimestamp: '2026-08-15T14:32:00Z', idempotencyKey: 'key-1' })

    await remove('nonexistent-key')

    expect(await listPending()).toHaveLength(1)
  })
})
