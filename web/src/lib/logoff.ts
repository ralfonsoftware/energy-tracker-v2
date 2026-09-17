import { listPending } from '@/lib/offline-queue'
import { flushQueue } from '@/lib/meter-reading-sync'

export interface PreLogoffQueueCheck {
  pendingCount: number
}

// AC #4: best-effort flush before logoff, then re-check what's actually still queued — a Meter
// Reading left in IndexedDB across a logoff risks posting under whichever Household logs in next
// on the same device (Dev Notes' flagged open design point). Only attempts a flush when the
// browser already knows it's online — offline, flushQueue()'s own fetch attempts would just fail
// after their timeout, adding latency to logoff with no chance of succeeding.
export async function checkPendingReadingsBeforeLogoff(householdId: string): Promise<PreLogoffQueueCheck> {
  if (typeof navigator !== 'undefined' && navigator.onLine) {
    await flushQueue(householdId)
  }

  // Only count entries belonging to the Household logging off — a stale entry left by a
  // different account on this device isn't this member's to be warned about (AC #4); it stays
  // queued, untouched, until that other Household's own session flushes it.
  const remaining = await listPending()
  return { pendingCount: remaining.filter((reading) => reading.householdId === householdId).length }
}
