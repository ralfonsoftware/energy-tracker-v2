import type { KeyboardEvent } from 'react'
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
import { AppearanceRow } from '@/components/preferences/appearance-row'

// Radix's menu content preventDefaults Tab and only arrow-navigates between menu items, which would
// leave the preference strips (radiogroups, not menu items) unreachable by keyboard. Make Tab /
// Shift+Tab cycle between each strip's checked segment and the first enabled menu item instead.
function cycleTabStops(event: KeyboardEvent<HTMLDivElement>) {
  if (event.key !== 'Tab') return
  const content = event.currentTarget
  const stops = [
    ...content.querySelectorAll<HTMLElement>('[role="radiogroup"] [role="radio"][tabindex="0"]'),
    ...[...content.querySelectorAll<HTMLElement>('[role="menuitem"]:not([data-disabled])')].slice(0, 1),
  ]
  const target = event.target as HTMLElement
  const current = stops.findIndex((stop) => stop === target || stop.closest('[role="radiogroup"]')?.contains(target))
  const fallback = current === -1 ? (event.shiftKey ? stops.length - 1 : 0) : current + (event.shiftKey ? -1 : 1)
  const next = stops[(fallback + stops.length) % stops.length]
  if (!next) return
  event.preventDefault()
  next.focus()
}

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
        <DropdownMenuContent align="end" className={cn(GLASS_DROPDOWN_CLASSNAME, 'w-[296px]')} onKeyDown={cycleTabStops}>
          {email && (
            <>
              <DropdownMenuLabel className="truncate font-normal text-foreground">{email}</DropdownMenuLabel>
              <DropdownMenuSeparator />
            </>
          )}
          {/* Story 8.10: preference rows (Appearance; 8.11 adds Language directly below). role="none" keeps
              the radiogroups out of the menu's direct-children semantics. Plain buttons, not menu items,
              so selecting a segment never closes the menu. */}
          <div role="none">
            <AppearanceRow />
          </div>
          <DropdownMenuSeparator />
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
