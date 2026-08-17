import { Plus } from 'lucide-react'
import { useTranslation } from 'react-i18next'
import { Button } from '@/components/ui/button'
import { LogReadingSheet } from '@/components/meter-reading/log-reading-sheet'
import { MeterRegressionPromptDialog } from '@/components/meter-reading/meter-regression-prompt-dialog'
import type { MeterRegressionPromptDto } from '@/lib/meter-regression-api'
import type { StatusDto } from '@/lib/status-api'
import { StatusCard } from './status-card'
import { NavChrome } from './nav-chrome'

interface DashboardHousehold {
  id: string
  locale: string
}

interface DashboardPageProps {
  household: DashboardHousehold
  status: StatusDto | null
  statusLoading: boolean
  playStatusEntranceAnimation: boolean
  logSheetOpen: boolean
  onLogSheetOpenChange: (open: boolean) => void
  onReadingSaved: () => void
  openRegressionPrompt: MeterRegressionPromptDto | null
  onRegressionResolved: () => void
  onSettingsClick: () => void
}

// The composed real Dashboard (mockups/key-dashboard.html): Status card as the first,
// highest-visual-weight element (AC #1, #10), the primary Log Reading action, and the bottom nav
// chrome. Deliberately does NOT render a Tariff Check prompt card — its due-date gating (FR-15)
// is Epic 5, not built yet; confirmed with Ralf during dev-story activation. InviteGeneratePanel
// (Story 1.8) is intentionally NOT rendered here either — it lived on this surface only because
// it predated a real Settings page; a code review of this story relocated it to SettingsPage so
// it stops competing with the Status card for visual weight (AC #10).
export function DashboardPage({
  household,
  status,
  statusLoading,
  playStatusEntranceAnimation,
  logSheetOpen,
  onLogSheetOpenChange,
  onReadingSaved,
  openRegressionPrompt,
  onRegressionResolved,
  onSettingsClick,
}: DashboardPageProps) {
  const { t } = useTranslation()

  // One LogReadingSheet instance — its trigger renders wherever this element is placed below,
  // and exactly one of the two placements ever mounts at a time (empty-state slot vs. below the
  // populated card), so the sheet/trigger is never duplicated.
  const logReadingSheet = (
    <LogReadingSheet
      trigger={
        <Button variant="glass-primary">
          <Plus className="size-4" aria-hidden="true" />
          {t('meterReading.trigger')}
        </Button>
      }
      open={logSheetOpen}
      onOpenChange={onLogSheetOpenChange}
      onSaved={onReadingSaved}
    />
  )

  const showEmptyState = !statusLoading && !status
  const showPopulated = !statusLoading && !!status

  return (
    <main className="flex min-h-svh flex-col gap-4 p-4">
      <h1 className="text-lg font-bold">{t('app.title')}</h1>

      <StatusCard
        status={status}
        loading={statusLoading}
        locale={household.locale}
        playEntranceAnimation={playStatusEntranceAnimation}
        emptyStateAction={showEmptyState ? logReadingSheet : undefined}
      />

      {showPopulated && <div className="flex justify-center">{logReadingSheet}</div>}

      <NavChrome active="dashboard" onDashboardClick={() => {}} onSettingsClick={onSettingsClick} />

      <MeterRegressionPromptDialog prompt={openRegressionPrompt} onResolved={onRegressionResolved} />
    </main>
  )
}
