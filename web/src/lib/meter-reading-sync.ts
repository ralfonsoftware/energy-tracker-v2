import {
  enqueue,
  isNeedsAttention,
  listPending,
  markRejected,
  remove,
  resubmit,
  type QueuedMeterReading,
  type RejectionInfo,
} from '@/lib/offline-queue'
import { apiFetch, toApiError } from '@/lib/api-client'

// A real error *response* (e.g. a 400 from MeterReadingValidationException) is surfaced to the caller
// as an ApiError, never queued, since a request that can never succeed must not sit in IndexedDB forever.
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

// householdId is a client-side-only queue tag (AC #4) — never sent on the wire. The API derives
// the Household from the authenticated session (AD-3), not from client input, so the request body
// only ever carries the three fields CreateMeterReading actually expects.
async function post(reading: QueuedMeterReading): Promise<Response> {
  return apiFetch('/api/meter-readings', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({
      kwhValue: reading.kwhValue,
      readingTimestamp: reading.readingTimestamp,
      idempotencyKey: reading.idempotencyKey,
    }),
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
// the user re-authenticates. 408 (Request Timeout) and 429 (Too Many Requests) are just as
// transient (a proxy can answer either): a plain retry fixes them, so they never put a reading in
// "needs attention" where the member could do nothing about it (Story 11.16, AC #5).
function isPermanentRejection(status: number): boolean {
  return status >= 400 && status < 500 && status !== 401 && status !== 403 && status !== 408 && status !== 429
}

// Reads the problem `detail` once from a rejected response; '' or whitespace counts as no reason.
async function rejectionOf(response: Response): Promise<{ status: number; detail: string | null }> {
  const error = await toApiError(response)
  return { status: response.status, detail: error.detail === null || error.detail.trim() === '' ? null : error.detail }
}

// Retries every pending reading through its own stored idempotencyKey (AD-16's no-op guarantee
// lands on retry) and removes it from the queue on any successful response, whether a fresh
// insert or a no-op replay. A response the server will never accept (e.g. failed validation) keeps
// the reading but marks it with the server's reason ("needs attention", Story 11.16, audit C4): the
// member typed it, so it is shown on the dashboard to correct and resend or to discard, never
// deleted here. flushQueue skips entries already marked, so a refused reading is not re-posted on
// every reconnect. A 5xx, an auth failure, a timeout (408/429) or a network-level failure is
// presumed transient and stays pending.
// `onSynced` fires once per successfully-sent reading — a background flush can create a regression
// prompt (Story 2.3) that nothing else would otherwise re-poll for, so callers that care about
// state made stale by a queued reading landing (e.g. the open-regression-prompt poll) hook in here.
export async function flushQueue(householdId: string, onSynced?: () => void): Promise<void> {
  const pending = await listPending()
  for (const reading of pending) {
    if (reading.householdId !== householdId) {
      // Left over from a different Household's session on this device (AC #4) — never post it
      // under the wrong account. It stays queued until that Household's own session flushes it.
      continue
    }
    if (isNeedsAttention(reading)) {
      continue
    }
    try {
      const response = await post(reading)
      if (response.ok) {
        await remove(reading.idempotencyKey)
        onSynced?.()
      } else if (isPermanentRejection(response.status)) {
        await markRejected(reading.idempotencyKey, await rejectionOf(response))
      }
    } catch {
      // Still offline (or a transient network failure) — leave it queued for the next flush.
    }
  }
}

export type ResendResult =
  | { outcome: 'sent' }
  | { outcome: 'rejected'; rejection: RejectionInfo }
  | { outcome: 'queued' }
  | { outcome: 'unavailable'; status: number }
  | { outcome: 'missing' }

// Corrects a reading the server refused and posts it again (Story 11.16, AC #3, #8). There is no
// parameter for the key: the stored idempotencyKey is the entry's identity (AD-16) and a correction
// of kWh or timestamp is payload, so a resend can never turn into a different reading. The entry
// becomes an ordinary pending one first, then goes out through the same post() as the flush; only a
// permanent refusal marks it again. A request that gets no answer is `queued` (it syncs on
// reconnect); a transient HTTP answer (5xx, 401, 403, 408, 429) is `unavailable` with its status, so
// the caller can say so while the entry stays pending (code review decision, AC #8 AMENDED). Only
// post() sits inside the "no answer" catch: a failing device store after the server answered
// propagates instead of being reported as "saved offline". It does not take flushQueue's
// one-at-a-time guard (a user action on one entry); a race with a flush over the same key posts
// twice at worst, a no-op by AD-16.
export async function resendRejectedReading(
  householdId: string,
  idempotencyKey: string,
  correction: { kwhValue: number; readingTimestamp: string },
): Promise<ResendResult> {
  const stored = (await listPending()).find((reading) => reading.idempotencyKey === idempotencyKey)
  if (stored === undefined || stored.householdId !== householdId) {
    return { outcome: 'missing' }
  }

  const corrected: QueuedMeterReading = {
    ...stored,
    kwhValue: correction.kwhValue,
    readingTimestamp: correction.readingTimestamp,
  }
  delete corrected.rejection
  // The entry can be discarded (another tab, leaving the Household) between the read above and this
  // write; then there is nothing to send, and a discarded reading must not reach the server.
  if (!(await resubmit(idempotencyKey, correction))) {
    return { outcome: 'missing' }
  }

  let response: Response
  try {
    response = await post(corrected)
  } catch {
    // Offline or a transient network failure: it stays pending and syncs on reconnect.
    return { outcome: 'queued' }
  }

  if (response.ok) {
    await remove(idempotencyKey)
    return { outcome: 'sent' }
  }
  if (isPermanentRejection(response.status)) {
    await markRejected(idempotencyKey, await rejectionOf(response))
    const marked = (await listPending()).find((reading) => reading.idempotencyKey === idempotencyKey)
    return marked?.rejection !== undefined ? { outcome: 'rejected', rejection: marked.rejection } : { outcome: 'missing' }
  }
  return { outcome: 'unavailable', status: response.status }
}

// Wires flushQueue() to reconnect events and an app-mount flush (covers reconnecting while the
// tab was closed). No Service Worker/Background Sync API — see Dev Notes for why a
// window-scoped listener plus an app-mount flush is sufficient for this story's AC. Guards
// against overlapping flushes (e.g. the mount call still in flight when 'online' fires) so two
// concurrent flushes never race to POST the same queued idempotencyKey at once.
export function registerOfflineSync(householdId: string, onSynced?: () => void): () => void {
  let flushing = false

  const runFlush = () => {
    if (flushing) {
      return
    }
    flushing = true
    void flushQueue(householdId, onSynced).finally(() => {
      flushing = false
    })
  }

  window.addEventListener('online', runFlush)
  runFlush()

  return () => window.removeEventListener('online', runFlush)
}
