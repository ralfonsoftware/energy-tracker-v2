import { useState } from 'react'
import { useTranslation } from 'react-i18next'
import { LogOut } from 'lucide-react'
import { Button } from '@/components/ui/button'
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
} from '@/components/ui/dialog'
import { GLASS_MODAL_CLASSNAME } from '@/lib/glass-classnames'
import { TaggingScaffoldManager } from '@/components/tagging-scaffold/tagging-scaffold-manager'
import { YearlyBaselineForm } from '@/components/yearly-baseline/yearly-baseline-form'
import { InviteGeneratePanel } from '@/components/household-invite/invite-generate-panel'
import { NavChrome } from '@/components/dashboard/nav-chrome'
import { checkPendingReadingsBeforeLogoff } from '@/lib/logoff'

interface SettingsPageProps {
  householdId: string
  supportsFederatedLogout: boolean
  onBack: () => void
  onTrendHistoryClick: () => void
  onTariffRadarClick: () => void
}

// FR-33/AC #1-#4: the three logoff dialog steps this control can walk through, in order —
// 'confirm' always shown first, then zero, one, or both warnings in sequence depending on what
// the pre-logoff checks find (a member with both queued readings and no federated-logout support
// sees 'queue-warning' first, then 'federated-warning' after confirming past it). 'closed' is the
// resting state.
type LogoffStep = 'closed' | 'confirm' | 'queue-warning' | 'federated-warning'

// Not yet the full Settings page EXPERIENCE.md's Information Architecture eventually describes
// (Tariff cadence, AI backend choice, data export/import) — those are later Epic 2+ stories. This
// page currently covers Room/Power Point/Device management (Story 1.9), Yearly Baseline (Story
// 2.1), member invitation (Story 1.8's InviteGeneratePanel, relocated here from the Dashboard
// placeholder shell by a code review of Story 2.5, once this page existed as a real destination),
// and Logoff / Account Switching (Story 1.12, FR-33) — the one control on this page reachable from
// every screen in exactly one further tap (AC #1), since Settings itself is one tap away via
// NavChrome's always-present bottom tab bar.
// Smart Plug Import moved OFF this page by Story 3.5 (FR-4 amendment, UX-DR20) — it's now a
// dedicated Dashboard-launched screen, not a Settings-embedded panel.
export function SettingsPage({ householdId, supportsFederatedLogout, onBack, onTrendHistoryClick, onTariffRadarClick }: SettingsPageProps) {
  const { t } = useTranslation()
  const [logoffStep, setLogoffStep] = useState<LogoffStep>('closed')
  const [logoffChecking, setLogoffChecking] = useState(false)
  const [pendingReadingCount, setPendingReadingCount] = useState(0)

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

  return (
    <main className="flex min-h-svh flex-col gap-6 p-4">
      <div className="flex items-center justify-between">
        <h1 className="text-2xl font-semibold">{t('settings.heading')}</h1>
        <Button variant="outline" onClick={onBack}>
          {t('settings.backToApp')}
        </Button>
      </div>

      <div className="flex flex-col gap-[var(--spacing-card-gap)]">
        <YearlyBaselineForm householdId={householdId} />
        <TaggingScaffoldManager />
        <InviteGeneratePanel />

        <Button variant="outline" className="self-start" onClick={() => setLogoffStep('confirm')}>
          <LogOut aria-hidden="true" />
          {t('settings.logoff.trigger')}
        </Button>
      </div>

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

      <NavChrome
        active="settings"
        onDashboardClick={onBack}
        onTrendHistoryClick={onTrendHistoryClick}
        onTariffRadarClick={onTariffRadarClick}
        onSettingsClick={() => {}}
      />
    </main>
  )
}
