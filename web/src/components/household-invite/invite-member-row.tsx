import { useState } from 'react'
import { useTranslation } from 'react-i18next'
import { Dialog, DialogContent, DialogHeader, DialogTitle } from '@/components/ui/dialog'
import { GLASS_MODAL_CLASSNAME } from '@/lib/glass-classnames'
import { InviteGeneratePanel } from './invite-generate-panel'

// Settings' "Household" section (Story 8.5, Task 2, AC #1): at >=660px a single row that opens the
// InviteGeneratePanel in a Dialog. Below 660px (Story 11.9, approved mock) the panel is rendered flat
// (`bare`) inside the Household card instead of as a nested glass card, so the members list and the
// invite action read as one section. Both variants dual-render (pure CSS toggle, since jsdom never
// evaluates media queries) rather than picking one via JS, matching nav-chrome.tsx's own
// wide:hidden/hidden wide:flex precedent. Mounting InviteGeneratePanel twice is safe — it fetches
// nothing on mount, only its own button click triggers a network call.
interface InviteMemberRowProps {
  // Story 11.9: forwarded to both panels so the open-invites list refetches after an invite was created.
  onCreated?: () => void
}

export function InviteMemberRow({ onCreated }: InviteMemberRowProps = {}) {
  const { t } = useTranslation()
  const [open, setOpen] = useState(false)

  return (
    <>
      <div className="wide:hidden">
        <InviteGeneratePanel bare onCreated={onCreated} />
      </div>

      <div className="hidden wide:block">
        {/* A plain bordered row, not a GlassCard: this sits inside the Household card (no card-in-card). */}
        <button
          type="button"
          aria-haspopup="dialog"
          className="flex w-full items-center justify-between rounded-[14px] border border-[rgba(40,70,50,0.12)] bg-[rgba(255,255,255,0.6)] px-3.5 py-3 text-sm font-semibold dark:border-[rgba(210,235,220,0.13)] dark:bg-[rgba(220,245,230,0.06)]"
          onClick={() => setOpen(true)}
        >
          <span>{t('householdInvite.generateButton')}</span>
          <span aria-hidden="true">→</span>
        </button>

        <Dialog open={open} onOpenChange={setOpen}>
          <DialogContent className={GLASS_MODAL_CLASSNAME}>
            <DialogHeader>
              <DialogTitle>{t('householdInvite.generateButton')}</DialogTitle>
            </DialogHeader>
            <InviteGeneratePanel onCreated={onCreated} />
          </DialogContent>
        </Dialog>
      </div>
    </>
  )
}
