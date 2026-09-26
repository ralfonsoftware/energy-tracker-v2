import { useCallback, useEffect, useRef, useState } from 'react'
import { Upload } from 'lucide-react'
import { useTranslation } from 'react-i18next'
import { GlassCard } from '@/components/ui/glass-card'
import { EventsCard } from '@/components/event/events-card'
import { MeterReadingsCard } from '@/components/meter-reading/meter-readings-card'
import { fetchStatusHistory, type StatusHistoryEntryDto } from '@/lib/status-api'
import { NavChrome } from '@/components/dashboard/nav-chrome'
import { TrendChart } from './trend-chart'
import { PerPlugDataCard } from './per-plug-data-card'

interface TrendHistoryPageProps {
  locale: string
  householdId: string
  supportsFederatedLogout: boolean
  email: string | null
  onBack: () => void
  onSettingsClick: () => void
  onTariffRadarClick: () => void
  onSmartPlugImportClick: () => void
}

// Shell mirrors SettingsPage — Trend History is a real nav-chrome tab (UX-DR9), unlike the
// standalone MeterReadingHistoryPage it absorbs (Story 2.8), which deliberately had no tab slot.
// Card order: chart, then Meter Readings — the two views of the same Main Meter data (FR-8), read
// as a pair. Events comes next (Story 6.2) — a persistent list is a better host for Story 6.3's
// "inline with the Event" correlation than the transient Log Event sheet. The Room -> Power Point
// -> Device tree (PerPlugDataCard) is a structurally different Smart Plug signal and stays last.
export function TrendHistoryPage({
  locale,
  householdId,
  supportsFederatedLogout,
  email,
  onBack,
  onSettingsClick,
  onTariffRadarClick,
  onSmartPlugImportClick,
}: TrendHistoryPageProps) {
  const { t } = useTranslation()
  const [entries, setEntries] = useState<StatusHistoryEntryDto[]>([])
  // Distinguishes "genuinely no history yet" from "the fetch failed" — without this a transient
  // error rendered the same empty-state copy as a brand-new household, with no error/retry signal
  // (the sibling MeterReadingsCard already makes this distinction for its own fetch).
  const [chartLoadError, setChartLoadError] = useState(false)

  // A ref-based request id, not a per-call `cancelled` closure — loadStatusHistory is now called
  // from two independent sites (mount, and MeterReadingsCard's onReadingCorrected after a save),
  // so a stale response from an earlier call must never overwrite state written by a later one,
  // regardless of which call site triggered which fetch or resolved first.
  const latestRequestId = useRef(0)

  const loadStatusHistory = useCallback(() => {
    const requestId = ++latestRequestId.current
    fetchStatusHistory()
      .then((result) => {
        if (latestRequestId.current === requestId) {
          setChartLoadError(false)
          setEntries(result)
        }
      })
      .catch(() => {
        if (latestRequestId.current === requestId) {
          setChartLoadError(true)
        }
      })
  }, [])

  useEffect(() => {
    loadStatusHistory()
    return () => {
      latestRequestId.current += 1
    }
  }, [loadStatusHistory])

  return (
    <main className="flex min-h-svh flex-col gap-4 p-4">
      {/* Story 8.3/Task 1: constrains the page's own content — header row and the card stack — to
          a centered 660px column at >=660px (UX-DR19), mirroring dashboard-page.tsx's Story 8.2
          wrapper verbatim. NavChrome is deliberately outside this wrapper: its top-nav variant is
          full-width by design (Story 8.1). */}
      <div data-slot="trend-history-content" className="flex flex-col gap-4 wide:mx-auto wide:w-full wide:max-w-[660px]">
        <div className="flex items-center justify-between">
          <h1 className="text-lg font-bold">{t('dashboard.nav.trendHistory')}</h1>
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

        <div className="flex flex-col gap-[var(--spacing-card-gap)]">
          <GlassCard>
            {chartLoadError ? (
              <p className="text-destructive text-sm">{t('trendHistory.chartLoadError')}</p>
            ) : (
              <TrendChart entries={entries} locale={locale} />
            )}
          </GlassCard>

          <MeterReadingsCard locale={locale} onReadingCorrected={loadStatusHistory} />

          <EventsCard locale={locale} />

          <PerPlugDataCard locale={locale} />
        </div>
      </div>

      <NavChrome
        active="trendHistory"
        onDashboardClick={onBack}
        onTrendHistoryClick={() => {}}
        onTariffRadarClick={onTariffRadarClick}
        onSettingsClick={onSettingsClick}
        householdId={householdId}
        supportsFederatedLogout={supportsFederatedLogout}
        email={email}
      />
    </main>
  )
}
