import { useTranslation } from 'react-i18next'
import { Button } from '@/components/ui/button'
import { TaggingScaffoldManager } from '@/components/tagging-scaffold/tagging-scaffold-manager'
import { YearlyBaselineForm } from '@/components/yearly-baseline/yearly-baseline-form'
import { InviteGeneratePanel } from '@/components/household-invite/invite-generate-panel'
import { NavChrome } from '@/components/dashboard/nav-chrome'

interface SettingsPageProps {
  householdId: string
  onBack: () => void
}

// Not yet the full Settings page EXPERIENCE.md's Information Architecture eventually describes
// (Tariff cadence, AI backend choice, data export/import) — those are later Epic 2+ stories. This
// page currently covers Room/Power Point/Device management (Story 1.9), Yearly Baseline (Story
// 2.1), and member invitation (Story 1.8's InviteGeneratePanel, relocated here from the Dashboard
// placeholder shell by a code review of Story 2.5, once this page existed as a real destination).
export function SettingsPage({ householdId, onBack }: SettingsPageProps) {
  const { t } = useTranslation()

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
      </div>

      <NavChrome active="settings" onDashboardClick={onBack} onSettingsClick={() => {}} />
    </main>
  )
}
