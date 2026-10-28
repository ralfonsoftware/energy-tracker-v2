import { useEffect, useMemo, useState } from 'react'
import { listNeedsAttention, QUEUE_CHANGED_EVENT, type QueuedMeterReading } from '@/lib/offline-queue'

// The Household's queued readings the server refused (Story 11.16), oldest first. Reads IndexedDB on
// mount, on a Household change and on QUEUE_CHANGED_EVENT, because a background flush
// (registerOfflineSync) runs outside React and can reject an entry while the Dashboard is open.
// A result that arrives after unmount, or after a newer read began, is dropped. An unreadable
// store (private browsing, blocked storage) is an empty list: the app works as before.
export function useRejectedReadings(householdId: string): QueuedMeterReading[] {
  const [loaded, setLoaded] = useState<QueuedMeterReading[]>([])

  useEffect(() => {
    let cancelled = false
    let latestRead = 0

    const read = () => {
      const thisRead = ++latestRead
      const apply = (readings: QueuedMeterReading[]) => {
        if (!cancelled && thisRead === latestRead) {
          setLoaded(readings)
        }
      }
      listNeedsAttention(householdId).then(apply, () => apply([]))
    }

    read()
    window.addEventListener(QUEUE_CHANGED_EVENT, read)
    return () => {
      cancelled = true
      window.removeEventListener(QUEUE_CHANGED_EVENT, read)
    }
  }, [householdId])

  // Until the read for a new Household lands, never show the previous Household's entries.
  return useMemo(() => loaded.filter((reading) => reading.householdId === householdId), [loaded, householdId])
}
