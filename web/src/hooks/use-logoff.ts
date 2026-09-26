import { useState } from 'react'
import { checkPendingReadingsBeforeLogoff } from '@/lib/logoff'

// FR-33/AC #1-#4: the three logoff dialog steps this control can walk through, in order —
// 'confirm' always shown first, then zero, one, or both warnings in sequence depending on what
// the pre-logoff checks find (a member with both queued readings and no federated-logout support
// sees 'queue-warning' first, then 'federated-warning' after confirming past it). 'closed' is the
// resting state.
export type LogoffStep = 'closed' | 'confirm' | 'queue-warning' | 'federated-warning'

export interface UseLogoffResult {
  logoffStep: LogoffStep
  logoffChecking: boolean
  pendingReadingCount: number
  openLogoffDialog: () => void
  closeLogoffDialog: () => void
  handleConfirmLogoff: () => Promise<void>
  proceedPastQueueCheck: () => void
  navigateToLogout: () => void
}

// Story 8.1/Task 4: extracted from SettingsPage (Story 1.12/FR-33) so a second entry point
// (ProfileMenu) can drive the exact same flow — no new logoff logic, only a second mounted
// <Dialog> consuming this same hook's state/handlers shape.
export function useLogoff(householdId: string, supportsFederatedLogout: boolean): UseLogoffResult {
  const [logoffStep, setLogoffStep] = useState<LogoffStep>('closed')
  const [logoffChecking, setLogoffChecking] = useState(false)
  const [pendingReadingCount, setPendingReadingCount] = useState(0)

  const openLogoffDialog = () => {
    setLogoffStep('confirm')
  }

  const closeLogoffDialog = () => {
    if (!logoffChecking) {
      setLogoffStep('closed')
    }
  }

  // A full page navigation, matching the existing `/login` pattern (App.tsx) — `/logout` must
  // carry the browser through the provider's own RP-initiated-logout redirect chain, which a
  // fetch() call cannot do (AC #2, #6).
  const navigateToLogout = () => {
    window.location.href = '/logout'
  }

  // Checked proactively (AC #3) — this app has no way to detect after the fact whether a
  // federated redirect happened, per AD-17's architecture note.
  const proceedPastQueueCheck = () => {
    if (!supportsFederatedLogout) {
      setLogoffStep('federated-warning')
      return
    }
    navigateToLogout()
  }

  // AC #4: best-effort flush, then surface whatever is still queued rather than silently
  // discarding it or letting it carry over to whichever Household logs in next on this device.
  const handleConfirmLogoff = async () => {
    setLogoffChecking(true)
    try {
      const { pendingCount } = await checkPendingReadingsBeforeLogoff(householdId)

      if (pendingCount > 0) {
        setPendingReadingCount(pendingCount)
        setLogoffStep('queue-warning')
        return
      }

      proceedPastQueueCheck()
    } catch {
      // Couldn't verify the offline queue (e.g. IndexedDB unavailable) — stay on the confirm
      // step rather than risk silently logging off past an unknown queue state (AC #4). The
      // `finally` below always re-enables the buttons so the member can retry or cancel.
    } finally {
      setLogoffChecking(false)
    }
  }

  return {
    logoffStep,
    logoffChecking,
    pendingReadingCount,
    openLogoffDialog,
    closeLogoffDialog,
    handleConfirmLogoff,
    proceedPastQueueCheck,
    navigateToLogout,
  }
}
