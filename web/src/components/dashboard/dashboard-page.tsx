import { useEffect, useState } from 'react'
import { NotebookPen, Plus, Upload } from 'lucide-react'
import { useTranslation } from 'react-i18next'
import { Button } from '@/components/ui/button'
import { LogReadingSheet } from '@/components/meter-reading/log-reading-sheet'
import { LogEventSheet } from '@/components/event/log-event-sheet'
import { MeterRegressionPromptDialog } from '@/components/meter-reading/meter-regression-prompt-dialog'
import type { MeterRegressionPromptDto } from '@/lib/meter-regression-api'
import type { StatusDto } from '@/lib/status-api'
import type { TariffCheckReminderDto } from '@/lib/tariff-check-api'
import { TariffCheckCard } from '@/components/tariff/tariff-check-card'
import { StatusCard } from './status-card'
import { StatusDetailDialog } from './status-detail-dialog'
import { NavChrome } from './nav-chrome'

interface DashboardHousehold {
  id: string
  locale: string
}

interface DashboardPageProps {
  household: DashboardHousehold
  supportsFederatedLogout: boolean
  email: string | null
  status: StatusDto | null
  statusLoading: boolean
  tariffCheck: TariffCheckReminderDto | null
  playStatusEntranceAnimation: boolean
  logSheetOpen: boolean
  onLogSheetOpenChange: (open: boolean) => void
  onReadingSaved: () => void
  logEventOpen: boolean
  onLogEventOpenChange: (open: boolean) => void
  openRegressionPrompt: MeterRegressionPromptDto | null
  onRegressionResolved: () => void
  onSettingsClick: () => void
  onTrendHistoryClick: () => void
  onTariffRadarClick: () => void
  onSmartPlugImportClick: () => void
}

// The composed real Dashboard (mockups/key-dashboard.html): Status card as the first,
// highest-visual-weight element (AC #1, #10), then the quiet Tariff Check prompt card (FR-15,
// Story 5.4), the primary Log Reading action, and the bottom nav chrome. InviteGeneratePanel
// (Story 1.8) is intentionally NOT rendered here either — it lived on this surface only because
// it predated a real Settings page; a code review of this story relocated it to SettingsPage so
// it stops competing with the Status card for visual weight (AC #10).
export function DashboardPage({
  household,
  supportsFederatedLogout,
  email,
  status,
  statusLoading,
  tariffCheck,
  playStatusEntranceAnimation,
  logSheetOpen,
  onLogSheetOpenChange,
  onReadingSaved,
  logEventOpen,
  onLogEventOpenChange,
  openRegressionPrompt,
  onRegressionResolved,
  onSettingsClick,
  onTrendHistoryClick,
  onTariffRadarClick,
  onSmartPlugImportClick,
}: DashboardPageProps) {
  const { t } = useTranslation()
  const [detailDialogOpen, setDetailDialogOpen] = useState(false)
  const [eventConfirmation, setEventConfirmation] = useState<string | null>(null)

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
      householdId={household.id}
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
  ) : undefined

  return (
    <main className="flex min-h-svh flex-col gap-4 p-4">
      {/* Story 8.2/Task 1: constrains the page's own content — header row, event confirmation,
          Status/Tariff-Check cards, and the Log Reading CTA — to a centered 660px column at
          >=660px (UX-DR19). NavChrome and the regression dialog are deliberately outside this
          wrapper (see story Dev Notes): NavChrome's top-nav variant is full-width by design
          (Story 8.1), and the dialog is an overlay with its own sizing. */}
      <div data-slot="dashboard-content" className="flex flex-col gap-4 wide:mx-auto wide:w-full wide:max-w-[660px]">
        <div className="flex items-center justify-between">
          <h1 className="text-lg font-bold">{t('app.title')}</h1>
          <div className="flex items-center gap-2">
            <LogEventSheet
              trigger={
                <button
                  type="button"
                  aria-label={t('event.entryPointLabel')}
                  title={t('event.entryPointLabel')}
                  className="bg-nav-chrome-active-bg text-nav-chrome-active-foreground flex size-10 shrink-0 items-center justify-center rounded-xl wide:size-auto wide:justify-start wide:gap-1.5 wide:px-3 wide:py-2"
                >
                  <NotebookPen className="size-4" aria-hidden="true" />
                  <span className="hidden wide:inline wide:text-xs wide:font-semibold">{t('event.shortLabel')}</span>
                </button>
              }
              open={logEventOpen}
              onOpenChange={onLogEventOpenChange}
              onSaved={(event) => setEventConfirmation(event.description)}
            />
            <button
              type="button"
              onClick={onSmartPlugImportClick}
              aria-label={t('smartPlugImport.entryPointLabel')}
              title={t('smartPlugImport.entryPointLabel')}
              className="bg-nav-chrome-active-bg text-nav-chrome-active-foreground flex size-10 shrink-0 items-center justify-center rounded-xl wide:size-auto wide:justify-start wide:gap-1.5 wide:px-3 wide:py-2"
            >
              <Upload className="size-4" aria-hidden="true" />
              <span className="hidden wide:inline wide:text-xs wide:font-semibold">{t('smartPlugImport.shortLabel')}</span>
            </button>
          </div>
        </div>

        {eventConfirmation && (
          // Rendered in the page body, not beside the topbar icons — an Event description runs to 500
          // characters and would otherwise distort the fixed-height header row.
          <p role="status" className="text-muted-foreground text-sm">
            {t('event.savedConfirmation', { description: eventConfirmation })}
          </p>
        )}

        <StatusCard
          status={status}
          loading={statusLoading}
          locale={household.locale}
          playEntranceAnimation={playStatusEntranceAnimation}
          emptyStateAction={showEmptyState ? logReadingSheet : undefined}
          detailTrigger={detailTrigger}
        />

        <TariffCheckCard reminder={tariffCheck} locale={household.locale} onClick={onTariffRadarClick} />

        {showPopulated && <div className="flex justify-center">{logReadingSheet}</div>}
      </div>

      <NavChrome
        active="dashboard"
        onDashboardClick={() => {}}
        onTrendHistoryClick={onTrendHistoryClick}
        onTariffRadarClick={onTariffRadarClick}
        onSettingsClick={onSettingsClick}
        householdId={household.id}
        supportsFederatedLogout={supportsFederatedLogout}
        email={email}
      />

      <MeterRegressionPromptDialog prompt={openRegressionPrompt} onResolved={onRegressionResolved} />
    </main>
  )
}
