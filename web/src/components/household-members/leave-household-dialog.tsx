import { useEffect, useRef, useState } from 'react'
import { useTranslation } from 'react-i18next'
import { TriangleAlert } from 'lucide-react'
import { Button } from '@/components/ui/button'
import { Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle } from '@/components/ui/dialog'
import { GLASS_MODAL_CLASSNAME } from '@/lib/glass-classnames'
import { checkPendingReadingsBeforeLogoff } from '@/lib/logoff'
import { removeForHousehold } from '@/lib/offline-queue'
import { ERR_BOX, ERR_BUTTON } from './styles'
import type { RemoveOutcome } from './use-household-members'

interface LeaveHouseholdDialogProps {
  householdId: string
  busy: boolean
  onClose: () => void
  onLeave: () => Promise<RemoveOutcome>
}

// Story 11.9 (AC #13, #15): leaving is the same DELETE as removing, with the caller's own member id. Mounted only while
// open, so every opening starts with a fresh check. The check reuses the logoff flow's flush-and-count; unsynced
// readings are deleted from this device only after the leave succeeded (a failed leave deletes nothing), and
// leaving ends with a full navigation to "/", which reloads the session and lands in the household-creation flow.
export function LeaveHouseholdDialog({ householdId, busy, onClose, onLeave }: LeaveHouseholdDialogProps) {
  const { t } = useTranslation()
  // null while the pending-readings check runs.
  const [pendingCount, setPendingCount] = useState<number | null>(null)
  const [error, setError] = useState<'generic' | 'lastMember' | null>(null)
  const [navigating, setNavigating] = useState(false)
  const cancelRef = useRef<HTMLButtonElement>(null)

  useEffect(() => {
    let cancelled = false
    checkPendingReadingsBeforeLogoff(householdId)
      .then(({ pendingCount: count }) => {
        if (!cancelled) setPendingCount(count)
      })
      // An unreadable offline queue must not trap the member in the household: leave without a warning.
      .catch(() => {
        if (!cancelled) setPendingCount(0)
      })
    return () => {
      cancelled = true
    }
  }, [householdId])

  const working = busy || navigating

  const confirm = async () => {
    setError(null)
    const outcome = await onLeave()
    if (outcome === 'removed') {
      setNavigating(true)
      try {
        await removeForHousehold(householdId)
      } catch {
        // The leave already succeeded; stale queue entries would only be refused by the server.
      }
      window.location.assign('/')
    } else if (outcome === 'changed') {
      onClose()
    } else {
      setError(outcome === 'last-member' ? 'lastMember' : 'generic')
    }
  }

  const checking = pendingCount === null
  const hasUnsynced = pendingCount !== null && pendingCount > 0
  const confirmLabel = checking
    ? t('householdMembers.checking')
    : working
      ? t('householdMembers.leaving')
      : hasUnsynced
        ? t('householdMembers.leaveAndDelete')
        : t('householdMembers.leaveConfirm')

  return (
    <Dialog open onOpenChange={(open) => !open && !working && onClose()}>
      <DialogContent
        className={GLASS_MODAL_CLASSNAME}
        onOpenAutoFocus={(event) => {
          event.preventDefault()
          cancelRef.current?.focus()
        }}
      >
        <DialogHeader>
          <DialogTitle>{t('householdMembers.leaveTitle')}</DialogTitle>
          <DialogDescription>{t('householdMembers.leaveBody')}</DialogDescription>
        </DialogHeader>
        {hasUnsynced && (
          <div role="note" className={`flex items-start gap-2 rounded-xl px-3 py-2 text-sm ${ERR_BOX}`}>
            <TriangleAlert aria-hidden="true" className="mt-0.5 size-4 shrink-0" />
            <span>
              <b>{t('householdMembers.unsyncedTitle')}</b>
              <br />
              {t('householdMembers.unsyncedBody', { count: pendingCount })}
            </span>
          </div>
        )}
        {error && (
          <p role="alert" className={`flex items-start gap-2 rounded-xl px-3 py-2 text-sm ${ERR_BOX}`}>
            <TriangleAlert aria-hidden="true" className="mt-0.5 size-4 shrink-0" />
            <span>{error === 'lastMember' ? t('householdMembers.error.lastMember') : t('householdMembers.error.leaveGeneric')}</span>
          </p>
        )}
        <DialogFooter>
          <Button ref={cancelRef} type="button" variant="outline" className="rounded-full" onClick={onClose} disabled={working}>
            {t('householdMembers.cancel')}
          </Button>
          <Button
            type="button"
            variant="destructive"
            className={`h-8 rounded-full px-4 font-bold ${ERR_BUTTON}`}
            onClick={() => void confirm()}
            disabled={working || checking || error === 'lastMember'}
          >
            {confirmLabel}
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  )
}
