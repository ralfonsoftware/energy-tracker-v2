import 'fake-indexeddb/auto'
import { act, renderHook, waitFor } from '@testing-library/react'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { IDBFactory } from 'fake-indexeddb'
import { enqueue, listNeedsAttention, markRejected, QUEUE_CHANGED_EVENT, type QueuedMeterReading } from '@/lib/offline-queue'
import { useRejectedReadings } from './use-rejected-readings'

// Everything is the real module except that listNeedsAttention is a pass-through spy, so a test can
// hold one read open (mockImplementationOnce) and release it later.
vi.mock('@/lib/offline-queue', async (importOriginal) => {
  const actual = await importOriginal<typeof import('@/lib/offline-queue')>()
  return { ...actual, listNeedsAttention: vi.fn(actual.listNeedsAttention) }
})

const householdId = '11111111-1111-1111-1111-111111111111'
const otherHouseholdId = '22222222-2222-2222-2222-222222222222'

async function seedRejected(key: string, readingTimestamp: string, owner = householdId) {
  await enqueue({ householdId: owner, kwhValue: 100, readingTimestamp, idempotencyKey: key })
  await markRejected(key, { status: 400, detail: 'nope' })
}

beforeEach(() => {
  globalThis.indexedDB = new IDBFactory()
})

afterEach(() => {
  vi.restoreAllMocks()
})

describe('useRejectedReadings', () => {
  it("loads the Household's needs-attention entries on mount, oldest first", async () => {
    await seedRejected('late', '2026-08-15T12:00:00Z')
    await seedRejected('early', '2026-08-15T06:00:00Z')
    await enqueue({ householdId, kwhValue: 1, readingTimestamp: '2026-08-15T07:00:00Z', idempotencyKey: 'pending' })

    const { result } = renderHook(() => useRejectedReadings(householdId))

    await waitFor(() => expect(result.current.map((r) => r.idempotencyKey)).toEqual(['early', 'late']))
  })

  it('re-reads when the queue changes (a background flush rejects an entry)', async () => {
    const { result } = renderHook(() => useRejectedReadings(householdId))
    await waitFor(() => expect(result.current).toEqual([]))

    await act(async () => {
      await seedRejected('k1', '2026-08-15T06:00:00Z')
    })

    await waitFor(() => expect(result.current.map((r) => r.idempotencyKey)).toEqual(['k1']))
  })

  it("ignores another Household's entries and re-reads when householdId changes", async () => {
    await seedRejected('mine', '2026-08-15T06:00:00Z')
    await seedRejected('theirs', '2026-08-15T07:00:00Z', otherHouseholdId)

    const { result, rerender } = renderHook(({ id }) => useRejectedReadings(id), { initialProps: { id: householdId } })
    await waitFor(() => expect(result.current.map((r) => r.idempotencyKey)).toEqual(['mine']))

    rerender({ id: otherHouseholdId })

    await waitFor(() => expect(result.current.map((r) => r.idempotencyKey)).toEqual(['theirs']))
  })

  it('resolves to an empty list when the store cannot be opened, without throwing', async () => {
    vi.spyOn(globalThis.indexedDB, 'open').mockImplementation(() => {
      throw new Error('blocked storage')
    })

    const { result } = renderHook(() => useRejectedReadings(householdId))

    await waitFor(() => expect(result.current).toEqual([]))
  })

  it('also resolves to an empty list when indexedDB.open reports an error event', async () => {
    vi.spyOn(globalThis.indexedDB, 'open').mockImplementation(() => {
      const request = {} as IDBOpenDBRequest
      queueMicrotask(() => request.onerror?.(new Event('error')))
      return request
    })

    const { result } = renderHook(() => useRejectedReadings(householdId))

    await waitFor(() => expect(result.current).toEqual([]))
  })

  it('stops listening after unmount: a later queue change starts no read', async () => {
    const { result, unmount } = renderHook(() => useRejectedReadings(householdId))
    await waitFor(() => expect(result.current).toEqual([]))
    const readsBefore = vi.mocked(listNeedsAttention).mock.calls.length

    unmount()
    window.dispatchEvent(new Event(QUEUE_CHANGED_EVENT))

    expect(vi.mocked(listNeedsAttention).mock.calls.length).toBe(readsBefore)
  })

  it('lets only the latest read win: a slow older read that finishes last is dropped', async () => {
    await seedRejected('fresh', '2026-08-15T06:00:00Z')
    const stale: QueuedMeterReading = { householdId, kwhValue: 1, readingTimestamp: '2026-08-15T05:00:00Z', idempotencyKey: 'stale' }
    let releaseStale: (readings: QueuedMeterReading[]) => void = () => {}
    vi.mocked(listNeedsAttention).mockImplementationOnce(() => new Promise((resolve) => (releaseStale = resolve)))

    // the mount read is held open; the queue-changed event starts a newer read that finishes first
    const { result } = renderHook(() => useRejectedReadings(householdId))
    act(() => {
      window.dispatchEvent(new Event(QUEUE_CHANGED_EVENT))
    })
    await waitFor(() => expect(result.current.map((r) => r.idempotencyKey)).toEqual(['fresh']))

    await act(async () => {
      releaseStale([stale])
    })

    expect(result.current.map((r) => r.idempotencyKey)).toEqual(['fresh'])
  })
})
