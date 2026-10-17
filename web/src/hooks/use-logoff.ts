import { useEffect, useState } from 'react'
import { apiFetch, toApiError } from '@/lib/api-client'
import { checkPendingReadingsBeforeLogoff } from '@/lib/logoff'

// FR-33/AC #1-#4: the three logoff dialog steps this control can walk through, in order —
// 'confirm' always shown first, then zero, one, or both warnings in sequence depending on what
// the pre-logoff checks find (a member with both queued readings and no federated-logout support
// sees 'queue-warning' first, then 'federated-warning' after confirming past it). 'closed' is the
// resting state.
export type LogoffStep = 'closed' | 'confirm' | 'queue-warning' | 'federated-warning'

export interface UseLogoffResult {
  logoffStep: LogoffStep
  // True while the offline-queue check or the POST /logout is running, and from a successful POST until the
  // browser has left the page, so no button can start a second request.
  logoffChecking: boolean
  // The last POST /logout failed (offline, 5xx, unreadable answer); the dialog stays open for a retry (AC #10).
  logoffError: boolean
  pendingReadingCount: number
  openLogoffDialog: () => void
  closeLogoffDialog: () => void
  handleConfirmLogoff: () => Promise<void>
  proceedPastQueueCheck: () => Promise<void>
  navigateToLogout: () => Promise<void>
}

// The server's answer is trusted, but the SPA still only follows a same-origin path or an http(s) URL. A
// leading `//` is protocol-relative and would resolve to another origin, so it falls back to "/" like
// javascript:, data: and anything unparseable. A value that starts with `/` must also *resolve* to this origin:
// the URL parser strips tabs and newlines, so "/<tab>/evil.example" becomes "//evil.example" after the prefix
// checks above have passed.
function toSafeNavigationTarget(value: unknown): string {
  if (typeof value !== 'string' || value === '' || value.startsWith('//') || value.startsWith('/\\')) {
    return '/'
  }

  try {
    const url = new URL(value, window.location.origin)
    if (url.protocol !== 'http:' && url.protocol !== 'https:') {
      return '/'
    }
    if (url.origin === window.location.origin) {
      return url.pathname + url.search + url.hash
    }
    return value.startsWith('/') ? '/' : url.href
  } catch {
    return '/'
  }
}

// Story 8.1/Task 4: extracted from SettingsPage (Story 1.12/FR-33) so a second entry point
// (ProfileMenu) can drive the exact same flow — no new logoff logic, only a second mounted
// <Dialog> consuming this same hook's state/handlers shape.
export function useLogoff(householdId: string, supportsFederatedLogout: boolean): UseLogoffResult {
  const [logoffStep, setLogoffStep] = useState<LogoffStep>('closed')
  const [checking, setChecking] = useState(false)
  const [navigating, setNavigating] = useState(false)
  const [logoffError, setLogoffError] = useState(false)
  const [pendingReadingCount, setPendingReadingCount] = useState(0)

  const logoffChecking = checking || navigating

  // Back from the provider's confirmation page can restore this page from the back/forward cache with
  // `navigating` still true, which would leave the dialog's buttons disabled until a reload.
  useEffect(() => {
    const onPageShow = (event: PageTransitionEvent) => {
      if (event.persisted) {
        setNavigating(false)
      }
    }
    window.addEventListener('pageshow', onPageShow)
    return () => window.removeEventListener('pageshow', onPageShow)
  }, [])

  const openLogoffDialog = () => {
    setLogoffError(false)
    setLogoffStep('confirm')
  }

  const closeLogoffDialog = () => {
    if (!logoffChecking) {
      setLogoffStep('closed')
    }
  }

  // Story 11.8 (AC #3, #5, #10; AD-27): sign-out is a POST that needs the CSRF header, so it goes through the
  // shared client. The server clears the cookie and answers { redirectUrl }, the provider's end-session URL
  // (or "/"); the SPA does the navigation itself. A form POST answered with a 302 would be blocked by the CSP's
  // form-action, and a fetch cannot follow the provider's cross-origin redirect. A failure leaves the dialog
  // where it is so the member can retry or cancel.
  const navigateToLogout = async () => {
    setChecking(true)
    setLogoffError(false)
    try {
      const response = await apiFetch('/logout', { method: 'POST' })
      if (!response.ok) {
        throw await toApiError(response)
      }
      const body = (await response.json()) as { redirectUrl?: unknown }
      window.location.href = toSafeNavigationTarget(body.redirectUrl)
      // Stay busy while the browser leaves the page.
      setNavigating(true)
    } catch {
      setLogoffError(true)
    } finally {
      setChecking(false)
    }
  }

  // Checked proactively (AC #3) — this app has no way to detect after the fact whether a
  // federated redirect happened, per AD-17's architecture note.
  const proceedPastQueueCheck = async () => {
    if (!supportsFederatedLogout) {
      setLogoffStep('federated-warning')
      return
    }
    await navigateToLogout()
  }

  // AC #4: best-effort flush, then surface whatever is still queued rather than silently
  // discarding it or letting it carry over to whichever Household logs in next on this device.
  const handleConfirmLogoff = async () => {
    setChecking(true)
    try {
      const { pendingCount } = await checkPendingReadingsBeforeLogoff(householdId)

      if (pendingCount > 0) {
        setPendingReadingCount(pendingCount)
        setLogoffStep('queue-warning')
        return
      }

      await proceedPastQueueCheck()
    } catch {
      // Couldn't verify the offline queue (e.g. IndexedDB unavailable) — stay on the confirm
      // step rather than risk silently logging off past an unknown queue state (AC #4). The
      // `finally` below always re-enables the buttons so the member can retry or cancel.
    } finally {
      setChecking(false)
    }
  }

  return {
    logoffStep,
    logoffChecking,
    logoffError,
    pendingReadingCount,
    openLogoffDialog,
    closeLogoffDialog,
    handleConfirmLogoff,
    proceedPastQueueCheck,
    navigateToLogout,
  }
}
