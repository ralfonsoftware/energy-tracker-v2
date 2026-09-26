import { User, LogOut } from 'lucide-react'
import { useTranslation } from 'react-i18next'
import { cn } from '@/lib/utils'
import { Button } from '@/components/ui/button'
import {
  DropdownMenu,
  DropdownMenuContent,
  DropdownMenuItem,
  DropdownMenuLabel,
  DropdownMenuSeparator,
  DropdownMenuTrigger,
} from '@/components/ui/dropdown-menu'
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
} from '@/components/ui/dialog'
import { GLASS_DROPDOWN_CLASSNAME, GLASS_MODAL_CLASSNAME } from '@/lib/glass-classnames'
import { useLogoff } from '@/hooks/use-logoff'

interface ProfileMenuProps {
  email: string | null
  householdId: string
  supportsFederatedLogout: boolean
}

// Story 8.1/Task 3: the top-nav-only counterpart to Settings' Logoff control — mounted solely
// inside NavChrome's wide:flex branch (no <660px equivalent per DESIGN/components.md). Drives the
// exact same shared use-logoff hook SettingsPage consumes, so "Log off" here runs the identical
// FR-33 flow, just from a second entry point (AC #4).
export function ProfileMenu({ email, householdId, supportsFederatedLogout }: ProfileMenuProps) {
  const { t } = useTranslation()
  const {
    logoffStep,
    logoffChecking,
    pendingReadingCount,
    openLogoffDialog,
    closeLogoffDialog,
    handleConfirmLogoff,
    proceedPastQueueCheck,
    navigateToLogout,
  } = useLogoff(householdId, supportsFederatedLogout)

  return (
    <>
      <DropdownMenu>
        <DropdownMenuTrigger asChild>
          <button
            type="button"
            aria-label={t('profileMenu.avatarLabel')}
            className="bg-nav-chrome-active-bg text-nav-chrome-active-foreground flex size-9 shrink-0 items-center justify-center rounded-full"
          >
            <User className="size-4" aria-hidden="true" />
          </button>
        </DropdownMenuTrigger>
        <DropdownMenuContent align="end" className={cn(GLASS_DROPDOWN_CLASSNAME, 'w-56')}>
          {email && (
            <>
              <DropdownMenuLabel className="truncate font-normal text-foreground">{email}</DropdownMenuLabel>
              <DropdownMenuSeparator />
            </>
          )}
          {/* Task 3: "Profile" has no defined destination anywhere in the PRD/epics/UX docs — a
              visibly present, non-interactive row rather than a silent no-op onClick (open
              question, noted in Completion Notes for a future story). */}
          <DropdownMenuItem disabled>{t('profileMenu.profile')}</DropdownMenuItem>
          <DropdownMenuItem
            variant="destructive"
            onSelect={(event) => {
              event.preventDefault()
              requestAnimationFrame(openLogoffDialog)
            }}
          >
            <LogOut aria-hidden="true" />
            {t('settings.logoff.trigger')}
          </DropdownMenuItem>
        </DropdownMenuContent>
      </DropdownMenu>

      <Dialog open={logoffStep !== 'closed'} onOpenChange={(open) => !open && closeLogoffDialog()}>
        <DialogContent className={GLASS_MODAL_CLASSNAME}>
          {logoffStep === 'confirm' && (
            <>
              <DialogHeader>
                <DialogTitle>{t('settings.logoff.confirmTitle')}</DialogTitle>
                <DialogDescription>{t('settings.logoff.confirmDescription')}</DialogDescription>
              </DialogHeader>
              <DialogFooter>
                <Button variant="outline" onClick={closeLogoffDialog} disabled={logoffChecking}>
                  {t('settings.logoff.cancel')}
                </Button>
                <Button variant="glass-confirm" onClick={handleConfirmLogoff} disabled={logoffChecking}>
                  {logoffChecking ? t('settings.logoff.checking') : t('settings.logoff.confirm')}
                </Button>
              </DialogFooter>
            </>
          )}

          {logoffStep === 'queue-warning' && (
            <>
              <DialogHeader>
                <DialogTitle>{t('settings.logoff.queueWarningTitle')}</DialogTitle>
                <DialogDescription>
                  {t('settings.logoff.queueWarningDescription', { count: pendingReadingCount })}
                </DialogDescription>
              </DialogHeader>
              <DialogFooter>
                <Button variant="outline" onClick={closeLogoffDialog}>
                  {t('settings.logoff.cancel')}
                </Button>
                <Button variant="glass-confirm" onClick={proceedPastQueueCheck}>
                  {t('settings.logoff.queueWarningProceed')}
                </Button>
              </DialogFooter>
            </>
          )}

          {logoffStep === 'federated-warning' && (
            <>
              <DialogHeader>
                <DialogTitle>{t('settings.logoff.federatedWarningTitle')}</DialogTitle>
                <DialogDescription>{t('settings.logoff.federatedWarningDescription')}</DialogDescription>
              </DialogHeader>
              <DialogFooter>
                <Button variant="outline" onClick={closeLogoffDialog}>
                  {t('settings.logoff.cancel')}
                </Button>
                <Button variant="glass-confirm" onClick={navigateToLogout}>
                  {t('settings.logoff.federatedWarningProceed')}
                </Button>
              </DialogFooter>
            </>
          )}
        </DialogContent>
      </Dialog>
    </>
  )
}
