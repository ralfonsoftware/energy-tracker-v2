// Thin wrapper over the browser's native indexedDB API (AD-16/NFR7). No wrapper library (e.g.
// `idb`) added for a single object store — this project's frontend dependency list is
// deliberately small (see Dev Notes).

export interface QueuedMeterReading {
  kwhValue: number
  readingTimestamp: string
  idempotencyKey: string
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

export async function enqueue(reading: QueuedMeterReading): Promise<void> {
  const db = await openDatabase()
  return new Promise((resolve, reject) => {
    const transaction = db.transaction(STORE_NAME, 'readwrite')
    transaction.objectStore(STORE_NAME).put(reading)
    transaction.oncomplete = () => resolve()
    transaction.onerror = () => reject(transaction.error)
  })
}

export async function listPending(): Promise<QueuedMeterReading[]> {
  const db = await openDatabase()
  return new Promise((resolve, reject) => {
    const transaction = db.transaction(STORE_NAME, 'readonly')
    const request = transaction.objectStore(STORE_NAME).getAll()
    request.onsuccess = () => resolve(request.result as QueuedMeterReading[])
    request.onerror = () => reject(request.error)
  })
}

export async function remove(idempotencyKey: string): Promise<void> {
  const db = await openDatabase()
  return new Promise((resolve, reject) => {
    const transaction = db.transaction(STORE_NAME, 'readwrite')
    transaction.objectStore(STORE_NAME).delete(idempotencyKey)
    transaction.oncomplete = () => resolve()
    transaction.onerror = () => reject(transaction.error)
  })
}
