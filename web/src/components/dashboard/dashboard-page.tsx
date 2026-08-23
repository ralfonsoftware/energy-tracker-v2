import { useEffect, useState } from 'react'
import { Plus } from 'lucide-react'
import { useTranslation } from 'react-i18next'
import { Button } from '@/components/ui/button'
import { LogReadingSheet } from '@/components/meter-reading/log-reading-sheet'
import { MeterRegressionPromptDialog } from '@/components/meter-reading/meter-regression-prompt-dialog'
import type { MeterRegressionPromptDto } from '@/lib/meter-regression-api'
import type { StatusDto } from '@/lib/status-api'
import { StatusCard } from './status-card'
import { StatusDetailDialog } from './status-detail-dialog'
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
  onHistoryClick: () => void
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
  onHistoryClick,
}: DashboardPageProps) {
  const { t } = useTranslation()
  const [detailDialogOpen, setDetailDialogOpen] = useState(false)

  // UX-DR13 (one-level-deep modal stacking): a newly-raised regression prompt supersedes this
  // read-only drill-down rather than stacking on top of it, the same discipline already applied
  // to the Log Reading sheet (App.tsx).
  useEffect(() => {
    if (openRegressionPrompt) {
      setDetailDialogOpen(false)
    }
  }, [openRegressionPrompt])

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

  // A transient status refresh failure (or the onboarding-empty state) unmounts the dialog by
  // dropping detailTrigger below — but detailDialogOpen lives here in the parent, so without this
  // it would silently reopen with a fresh fetch the next time status repopulates.
  useEffect(() => {
    if (!showPopulated) {
      setDetailDialogOpen(false)
    }
  }, [showPopulated])

  const detailTrigger = showPopulated ? (
    <>
      <StatusDetailDialog
        open={detailDialogOpen}
        onOpenChange={setDetailDialogOpen}
        locale={household.locale}
        trigger={
          <button
            type="button"
            className="mt-3 text-xs font-medium text-muted-foreground underline underline-offset-4 hover:text-foreground"
          >
            {t('dashboard.statusDetail.trigger')}
          </button>
        }
      />
      <button
        type="button"
        onClick={onHistoryClick}
        className="mt-1 block text-xs font-medium text-muted-foreground underline underline-offset-4 hover:text-foreground"
      >
        {t('dashboard.historyTrigger')}
      </button>
    </>
  ) : undefined

  return (
    <main className="flex min-h-svh flex-col gap-4 p-4">
      <h1 className="text-lg font-bold">{t('app.title')}</h1>

      <StatusCard
        status={status}
        loading={statusLoading}
        locale={household.locale}
        playEntranceAnimation={playStatusEntranceAnimation}
        emptyStateAction={showEmptyState ? logReadingSheet : undefined}
        detailTrigger={detailTrigger}
      />

      {showPopulated && <div className="flex justify-center">{logReadingSheet}</div>}

      <NavChrome active="dashboard" onDashboardClick={() => {}} onSettingsClick={onSettingsClick} />

      <MeterRegressionPromptDialog prompt={openRegressionPrompt} onResolved={onRegressionResolved} />
    </main>
  )
}
