import { useTranslation } from 'react-i18next'
import { Button } from '@/components/ui/button'
import { TaggingScaffoldManager } from '@/components/tagging-scaffold/tagging-scaffold-manager'
import { YearlyBaselineForm } from '@/components/yearly-baseline/yearly-baseline-form'

interface SettingsPageProps {
  householdId: string
  onBack: () => void
}

// Not yet the full Settings page EXPERIENCE.md's Information Architecture eventually describes
// (Tariff cadence, AI backend choice, data export/import, member invitation) — those are later
// Epic 2+ stories. This page currently covers Room/Power Point/Device management (Story 1.9) and
// Yearly Baseline (Story 2.1).
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
      </div>
    </main>
  )
}
