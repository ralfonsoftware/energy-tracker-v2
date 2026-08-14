import { useTranslation } from 'react-i18next'
import { Button } from '@/components/ui/button'
import { TaggingScaffoldManager } from '@/components/tagging-scaffold/tagging-scaffold-manager'

interface SettingsPageProps {
  onBack: () => void
}

// The minimum surface that satisfies AC #1's "reached via Settings" — not the full Settings page
// EXPERIENCE.md's Information Architecture eventually describes (Yearly Baseline, Tariff cadence,
// AI backend choice, data export/import, member invitation). None of those exist as features yet;
// Epic 2+ builds them. This story only adds the Room/Power Point/Device management slice.
export function SettingsPage({ onBack }: SettingsPageProps) {
  const { t } = useTranslation()

  return (
    <main className="flex min-h-svh flex-col gap-6 p-4">
      <div className="flex items-center justify-between">
        <h1 className="text-2xl font-semibold">{t('settings.heading')}</h1>
        <Button variant="outline" onClick={onBack}>
          {t('settings.backToApp')}
        </Button>
      </div>

      <TaggingScaffoldManager />
    </main>
  )
}
