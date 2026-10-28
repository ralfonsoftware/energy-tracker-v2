// Thin wrapper over the browser's native indexedDB API (AD-16/NFR7). No wrapper library (e.g.
// `idb`) added for a single object store — this project's frontend dependency list is
// deliberately small (see Dev Notes).

// Story 11.16 (audit C4): why the server refused a queued reading. The entry stays in the store
// with this field set ("needs attention") until the member corrects and resends it or discards it.
export interface RejectionInfo {
  status: number
  // The RFC 7807 `detail` the API returned, verbatim; null when the response had none.
  detail: string | null
  rejectedAtUtc: string
}

export interface QueuedMeterReading {
  kwhValue: number
  readingTimestamp: string
  idempotencyKey: string
  // Captured at enqueue time from the session that created the reading — lets flushQueue refuse
  // to post an entry under a *different* Household's session if another account logs in on this
  // device before the entry syncs (AC #4, Story 1.12's cross-Household misattribution fix).
  householdId: string
  // Set once the server permanently refused this entry (Story 11.16). An entry with `rejection`
  // is *needs attention*, one without is *pending*; entries written before that story have none.
  rejection?: RejectionInfo
}

// Dispatched on `window` after every change to the store, so a reader outside React's data flow
// (the dashboard's rejected-readings hook) can re-read when a background flush rejects an entry.
export const QUEUE_CHANGED_EVENT = 'energy-tracker:offline-queue-changed'

function notifyQueueChanged(): void {
  if (typeof window !== 'undefined') {
    window.dispatchEvent(new Event(QUEUE_CHANGED_EVENT))
  }
}

// A transaction can abort without any request reporting an error (quota exceeded at commit, an
// exception thrown inside a handler): without this the promise would never settle and a flush or a
// resend would hang for good (Story 11.16 code review).
function rejectOnFailure(transaction: IDBTransaction, reject: (reason: unknown) => void): void {
  transaction.onerror = () => reject(transaction.error)
  transaction.onabort = () => reject(transaction.error ?? new Error('The offline queue transaction was aborted'))
}

const DB_NAME = 'energy-tracker-offline-queue'
const DB_VERSION = 1
const STORE_NAME = 'pending-meter-readings'

function openDatabase(): Promise<IDBDatabase> {
  return new Promise((resolve, reject) => {
    const request = indexedDB.open(DB_NAME, DB_VERSION)

    request.onupgradeneeded = () => {
      const db = request.result
      if (!db.objectStoreNames.contains(STORE_NAME)) {
        db.createObjectStore(STORE_NAME, { keyPath: 'idempotencyKey' })
      }
    }

    request.onsuccess = () => resolve(request.result)
    request.onerror = () => reject(request.error)
  })
}

export function isNeedsAttention(reading: QueuedMeterReading): boolean {
  return reading.rejection !== undefined
}

export async function enqueue(reading: QueuedMeterReading): Promise<void> {
  const db = await openDatabase()
  return new Promise((resolve, reject) => {
    const transaction = db.transaction(STORE_NAME, 'readwrite')
    transaction.objectStore(STORE_NAME).put(reading)
    transaction.oncomplete = () => {
      notifyQueueChanged()
      resolve()
    }
    rejectOnFailure(transaction, reject)
  })
}

// Every entry of the store, pending and needs-attention alike. Callers want exactly that: the
// logoff check counts what is still on the device and removeForHousehold must delete rejected
// entries too (Story 11.9). Use listNeedsAttention() for the rejected ones only.
export async function listPending(): Promise<QueuedMeterReading[]> {
  const db = await openDatabase()
  return new Promise((resolve, reject) => {
    const transaction = db.transaction(STORE_NAME, 'readonly')
    const request = transaction.objectStore(STORE_NAME).getAll()
    request.onsuccess = () => resolve(request.result as QueuedMeterReading[])
    request.onerror = () => reject(request.error)
  })
}

// The Household's entries the server refused, oldest reading first (Story 11.16).
export async function listNeedsAttention(householdId: string): Promise<QueuedMeterReading[]> {
  const all = await listPending()
  return all
    .filter((reading) => reading.householdId === householdId && isNeedsAttention(reading))
    .sort((a, b) => Date.parse(a.readingTimestamp) - Date.parse(b.readingTimestamp))
}

// One readwrite transaction for the read and the write: awaiting anything between `get` and `put`
// would let the transaction auto-commit and the `put` would throw TransactionInactiveError. An entry
// that no longer exists (discarded, or removeForHousehold ran while a POST was in flight) is left
// alone and nothing is dispatched — a missing key must never be resurrected. Resolves true when an
// entry was changed, false when there was none.
async function updateEntry(
  idempotencyKey: string,
  update: (existing: QueuedMeterReading) => QueuedMeterReading,
): Promise<boolean> {
  const db = await openDatabase()
  return new Promise((resolve, reject) => {
    const transaction = db.transaction(STORE_NAME, 'readwrite')
    const store = transaction.objectStore(STORE_NAME)
    let changed = false
    const request = store.get(idempotencyKey)
    request.onsuccess = () => {
      const existing = request.result as QueuedMeterReading | undefined
      if (existing === undefined) {
        return
      }
      store.put(update(existing))
      changed = true
    }
    transaction.oncomplete = () => {
      if (changed) {
        notifyQueueChanged()
      }
      resolve(changed)
    }
    rejectOnFailure(transaction, reject)
  })
}

// Keeps the entry and marks why the server refused it (Story 11.16, audit C4).
export function markRejected(idempotencyKey: string, rejection: { status: number; detail: string | null }): Promise<boolean> {
  return updateEntry(idempotencyKey, (existing) => ({
    ...existing,
    rejection: { status: rejection.status, detail: rejection.detail, rejectedAtUtc: new Date().toISOString() },
  }))
}

// Stores the member's correction under the same idempotencyKey and makes the entry an ordinary
// pending one again. The key is the entry's identity (AD-16); kWh and timestamp are payload.
export function resubmit(idempotencyKey: string, correction: { kwhValue: number; readingTimestamp: string }): Promise<boolean> {
  return updateEntry(idempotencyKey, (existing) => {
    const corrected: QueuedMeterReading = {
      ...existing,
      kwhValue: correction.kwhValue,
      readingTimestamp: correction.readingTimestamp,
    }
    delete corrected.rejection
    return corrected
  })
}

export async function remove(idempotencyKey: string): Promise<void> {
  const db = await openDatabase()
  return new Promise((resolve, reject) => {
    const transaction = db.transaction(STORE_NAME, 'readwrite')
    const store = transaction.objectStore(STORE_NAME)
    let existed = false
    const lookup = store.getKey(idempotencyKey)
    lookup.onsuccess = () => {
      existed = lookup.result !== undefined
      store.delete(idempotencyKey)
    }
    transaction.oncomplete = () => {
      if (existed) {
        notifyQueueChanged()
      }
      resolve()
    }
    rejectOnFailure(transaction, reject)
  })
}

// Story 11.9 (AC #13b): leaving a Household discards that Household's unsynced readings from this device.
// Entries of other Households stay: they flush under their own session (AC #4, Story 1.12).
// Rejected entries go too (Story 11.16): listPending() returns everything on the device.
export async function removeForHousehold(householdId: string): Promise<void> {
  const pending = await listPending()
  for (const reading of pending) {
    if (reading.householdId === householdId) {
      await remove(reading.idempotencyKey)
    }
  }
}
