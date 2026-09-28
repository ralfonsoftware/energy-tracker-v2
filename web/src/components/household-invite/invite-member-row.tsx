import { useState } from 'react'
import { useTranslation } from 'react-i18next'
import { GlassCard } from '@/components/ui/glass-card'
import { Dialog, DialogContent, DialogHeader, DialogTitle } from '@/components/ui/dialog'
import { GLASS_MODAL_CLASSNAME } from '@/lib/glass-classnames'
import { InviteGeneratePanel } from './invite-generate-panel'

// Settings' "Household" section (Story 8.5, Task 2, AC #1): below 660px, the existing standalone
// InviteGeneratePanel card is unchanged; at >=660px it becomes a single row that opens the same
// panel in a Dialog instead. Both variants dual-render (pure CSS toggle, since jsdom never
// evaluates media queries) rather than picking one via JS, matching nav-chrome.tsx's own
// wide:hidden/hidden wide:flex precedent. Mounting InviteGeneratePanel twice is safe — it fetches
// nothing on mount, only its own button click triggers a network call.
export function InviteMemberRow() {
  const { t } = useTranslation()
  const [open, setOpen] = useState(false)

  return (
    <>
      <div className="wide:hidden">
        <InviteGeneratePanel />
      </div>

      <div className="hidden wide:block">
        <GlassCard className="gap-0 p-0">
          <button
            type="button"
            aria-haspopup="dialog"
            className="flex w-full items-center justify-between px-3.5 py-3 text-sm font-semibold"
            onClick={() => setOpen(true)}
          >
            <span>{t('householdInvite.generateButton')}</span>
            <span aria-hidden="true">→</span>
          </button>
        </GlassCard>

        <Dialog open={open} onOpenChange={setOpen}>
          <DialogContent className={GLASS_MODAL_CLASSNAME}>
            <DialogHeader>
              <DialogTitle>{t('householdInvite.generateButton')}</DialogTitle>
            </DialogHeader>
            <InviteGeneratePanel />
          </DialogContent>
        </Dialog>
      </div>
    </>
  )
}
