import { useRef } from 'react'
import { useTranslation } from 'react-i18next'
import { TriangleAlert } from 'lucide-react'
import { Button } from '@/components/ui/button'
import { Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle } from '@/components/ui/dialog'
import { GLASS_MODAL_CLASSNAME } from '@/lib/glass-classnames'
import { ERR_BOX, ERR_BUTTON } from './styles'
import type { HouseholdMember } from '@/lib/household-members-api'

interface RemoveMemberDialogProps {
  member: HouseholdMember | null
  // The display label (the generic label for a member without a name); the request carries the member id, never this.
  label: string
  joinedOn: string
  busy: boolean
  error: 'generic' | 'lastMember' | null
  onCancel: () => void
  onConfirm: () => void
}

// Story 11.9 (AC #15): the confirm step before removing another member. Says what really happens: access ends now,
// every open invite link stops working, the household's data stays. "Member since" tells same-named members apart.
export function RemoveMemberDialog({ member, label, joinedOn, busy, error, onCancel, onConfirm }: RemoveMemberDialogProps) {
  const { t } = useTranslation()
  const cancelRef = useRef<HTMLButtonElement>(null)

  return (
    <Dialog open={member !== null} onOpenChange={(open) => !open && !busy && onCancel()}>
      <DialogContent
        className={GLASS_MODAL_CLASSNAME}
        onOpenAutoFocus={(event) => {
          // Focus starts on the safe choice.
          event.preventDefault()
          cancelRef.current?.focus()
        }}
      >
        <DialogHeader>
          <DialogTitle>{t('householdMembers.removeTitle', { name: label })}</DialogTitle>
          <DialogDescription>{t('householdMembers.removeBody', { name: label })}</DialogDescription>
        </DialogHeader>
        <p className="text-xs text-muted-foreground">{t('householdMembers.memberSince', { date: joinedOn })}</p>
        {error && (
          <p role="alert" className={`flex items-start gap-2 rounded-xl px-3 py-2 text-sm ${ERR_BOX}`}>
            <TriangleAlert aria-hidden="true" className="mt-0.5 size-4 shrink-0" />
            <span>
              {error === 'lastMember' ? t('householdMembers.error.lastMember') : t('householdMembers.error.removeGeneric', { name: label })}
            </span>
          </p>
        )}
        <DialogFooter>
          <Button ref={cancelRef} type="button" variant="outline" className="rounded-full" onClick={onCancel} disabled={busy}>
            {t('householdMembers.cancel')}
          </Button>
          <Button
            type="button"
            variant="destructive"
            className={`h-8 rounded-full px-4 font-bold ${ERR_BUTTON}`}
            onClick={onConfirm}
            disabled={busy || error === 'lastMember'}
          >
            {busy ? t('householdMembers.removing') : t('householdMembers.removeConfirm')}
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  )
}
