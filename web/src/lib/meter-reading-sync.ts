import { enqueue, listPending, remove, type QueuedMeterReading } from '@/lib/offline-queue'

// Same shape as tagging-scaffold-manager.tsx's ApiError/toApiError pattern (Dev Notes) — a real
// error *response* (e.g. a 400 from MeterReadingValidationException) is surfaced to the caller,
// never queued, since a request that can never succeed must not sit in IndexedDB forever.
export class ApiError extends Error {
  status: number
  detail: string | null

  constructor(status: number, detail: string | null) {
    super(`Request failed with status ${status}`)
    this.status = status
    this.detail = detail
  }
}

async function toApiError(response: Response): Promise<ApiError> {
  try {
    const body = (await response.json()) as { detail?: string }
    return new ApiError(response.status, body.detail ?? null)
  } catch {
    return new ApiError(response.status, null)
  }
}

export interface MeterReadingDto {
  id: string
  kwhValue: number
  readingTimestamp: string
}

export type SendResult = { outcome: 'sent'; reading: MeterReadingDto } | { outcome: 'queued' }

// A bounded timeout so a live-but-dead connection (NFR7's "signal-weak basement" scenario) falls
// back to the offline queue promptly instead of hanging indefinitely — AbortSignal.timeout makes
// fetch() reject, which both call sites below already treat as "couldn't reach the server."
const REQUEST_TIMEOUT_MS = 10_000

async function post(reading: QueuedMeterReading): Promise<Response> {
  return fetch('/api/meter-readings', {
    method: 'POST',
    credentials: 'include',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(reading),
    signal: AbortSignal.timeout(REQUEST_TIMEOUT_MS),
  })
}

// Posts a reading; a network-level failure (offline, fetch throws) enqueues it for later instead
// of surfacing an error — a genuine error *response* (e.g. 400) is a different case and is thrown.
export async function attemptSend(reading: QueuedMeterReading): Promise<SendResult> {
  if (typeof navigator !== 'undefined' && navigator.onLine === false) {
    await enqueue(reading)
    return { outcome: 'queued' }
  }

  let response: Response
  try {
    response = await post(reading)
  } catch {
    await enqueue(reading)
    return { outcome: 'queued' }
  }

  if (!response.ok) {
    throw await toApiError(response)
  }

  const body = (await response.json()) as MeterReadingDto
  return { outcome: 'sent', reading: body }
}

// A 401/403 means the current session can't authorize the request right now (e.g. an expired
// cookie) — not that this reading's payload is invalid — so it must stay queued for a retry after
// the user re-authenticates, not be discarded like a genuine validation failure.
function isPermanentRejection(status: number): boolean {
  return status >= 400 && status < 500 && status !== 401 && status !== 403
}

// Retries every queued reading through its own stored idempotencyKey (AD-16's no-op guarantee
// lands on retry) and removes it from the queue on any successful response, whether a fresh
// insert or a no-op replay. A response the server will never accept (e.g. failed validation)
// is removed too — otherwise it would retry forever with no way for the user to see or clear it.
// A 5xx, an auth failure, or a network-level failure is presumed transient and stays queued.
export async function flushQueue(): Promise<void> {
  const pending = await listPending()
  for (const reading of pending) {
    try {
      const response = await post(reading)
      if (response.ok) {
        await remove(reading.idempotencyKey)
      } else if (isPermanentRejection(response.status)) {
        const error = await toApiError(response)
        console.error(`Discarding queued meter reading that the server rejected: ${error.detail ?? error.message}`, reading)
        await remove(reading.idempotencyKey)
      }
    } catch {
      // Still offline (or a transient network failure) — leave it queued for the next flush.
    }
  }
}

// Wires flushQueue() to reconnect events and an app-mount flush (covers reconnecting while the
// tab was closed). No Service Worker/Background Sync API — see Dev Notes for why a
// window-scoped listener plus an app-mount flush is sufficient for this story's AC. Guards
// against overlapping flushes (e.g. the mount call still in flight when 'online' fires) so two
// concurrent flushes never race to POST the same queued idempotencyKey at once.
export function registerOfflineSync(): () => void {
  let flushing = false

  const runFlush = () => {
    if (flushing) {
      return
    }
    flushing = true
    void flushQueue().finally(() => {
      flushing = false
    })
  }

  window.addEventListener('online', runFlush)
  runFlush()

  return () => window.removeEventListener('online', runFlush)
}
