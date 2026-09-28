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
import { AiPlausibilityForm } from '@/components/ai-plausibility/ai-plausibility-form'
import { DataExportPanel } from '@/components/data-export/data-export-panel'
import { DataImportPanel } from '@/components/data-import/data-import-panel'
import { InviteMemberRow } from '@/components/household-invite/invite-member-row'
import { NavChrome } from '@/components/dashboard/nav-chrome'
import { useLogoff } from '@/hooks/use-logoff'
import { SectionLabel } from './section-label'

interface SettingsPageProps {
  householdId: string
  supportsFederatedLogout: boolean
  email: string | null
  onBack: () => void
  onTrendHistoryClick: () => void
  onTariffRadarClick: () => void
}

// Not yet the full Settings page EXPERIENCE.md's Information Architecture eventually describes
// (Tariff cadence, AI backend choice) — those are later stories. This page currently covers
// Room/Power Point/Device management (Story 1.9), Yearly Baseline (Story 2.1), member invitation
// (Story 1.8's InviteGeneratePanel, relocated here from the Dashboard placeholder shell by a code
// review of Story 2.5, once this page existed as a real destination), Logoff / Account Switching
// (Story 1.12, FR-33) — the one control on this page reachable from every screen in exactly one
// further tap (AC #1), since Settings itself is one tap away via NavChrome's always-present
// bottom tab bar — and full Data Export/Import (Story 7.1's DataExportPanel, Story 7.2's
// DataImportPanel, UX-DR12).
// Smart Plug Import moved OFF this page by Story 3.5 (FR-4 amendment, UX-DR20) — it's now a
// dedicated Dashboard-launched screen, not a Settings-embedded panel.
export function SettingsPage({ householdId, supportsFederatedLogout, email, onBack, onTrendHistoryClick, onTariffRadarClick }: SettingsPageProps) {
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
    <main className="flex min-h-svh flex-col gap-6 p-4">
      <div data-slot="settings-content" className="flex flex-col gap-6 wide:mx-auto wide:w-full wide:max-w-[660px]">
        <div className="flex items-center justify-between">
          <h1 className="text-2xl font-semibold">{t('settings.heading')}</h1>
          <Button variant="outline" onClick={onBack}>
            {t('settings.backToApp')}
          </Button>
        </div>

        <div className="flex flex-col gap-[var(--spacing-card-gap)] wide:order-1">
          <SectionLabel>{t('yearlyBaseline.heading')}</SectionLabel>
          <YearlyBaselineForm householdId={householdId} />
        </div>
        <div className="flex flex-col gap-[var(--spacing-card-gap)] wide:order-2">
          <SectionLabel>{t('settings.sections.aiPlausibilityCheck')}</SectionLabel>
          <AiPlausibilityForm householdId={householdId} />
        </div>
        <div className="flex flex-col gap-[var(--spacing-card-gap)] wide:order-3">
          <SectionLabel>{t('settings.sections.household')}</SectionLabel>
          <InviteMemberRow />
        </div>
        <div className="flex flex-col gap-[var(--spacing-card-gap)] wide:order-4">
          <SectionLabel>{t('taggingScaffold.heading')}</SectionLabel>
          <TaggingScaffoldManager />
        </div>
        <div className="flex flex-col gap-[var(--spacing-card-gap)] wide:order-5">
          <SectionLabel>{t('settings.sections.data')}</SectionLabel>
          <DataExportPanel />
          <DataImportPanel />
        </div>

        <Button variant="outline" className="wide:hidden self-start" onClick={openLogoffDialog}>
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
        householdId={householdId}
        supportsFederatedLogout={supportsFederatedLogout}
        email={email}
      />
    </main>
  )
}
