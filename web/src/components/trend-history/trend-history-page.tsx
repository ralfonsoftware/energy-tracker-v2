import { useEffect, useState } from 'react'
import { Upload } from 'lucide-react'
import { useTranslation } from 'react-i18next'
import { GlassCard } from '@/components/ui/glass-card'
import { MeterReadingsCard } from '@/components/meter-reading/meter-readings-card'
import { fetchStatusHistory, type StatusHistoryEntryDto } from '@/lib/status-api'
import { NavChrome } from '@/components/dashboard/nav-chrome'
import { TrendChart } from './trend-chart'

interface TrendHistoryPageProps {
  locale: string
  onBack: () => void
  onSettingsClick: () => void
  onSmartPlugImportClick: () => void
}

// Shell mirrors SettingsPage — Trend History is a real nav-chrome tab (UX-DR9), unlike the
// standalone MeterReadingHistoryPage it absorbs (Story 2.8), which deliberately had no tab slot.
// Card order: chart, then Meter Readings — the two views of the same Main Meter data (FR-8), read
// as a pair. The Room -> Power Point -> Device tree (Story 4.2) is a structurally different Smart
// Plug signal and stays last, not added here.
export function TrendHistoryPage({ locale, onBack, onSettingsClick, onSmartPlugImportClick }: TrendHistoryPageProps) {
  const { t } = useTranslation()
  const [entries, setEntries] = useState<StatusHistoryEntryDto[]>([])
  // Distinguishes "genuinely no history yet" from "the fetch failed" — without this a transient
  // error rendered the same empty-state copy as a brand-new household, with no error/retry signal
  // (the sibling MeterReadingsCard already makes this distinction for its own fetch).
  const [chartLoadError, setChartLoadError] = useState(false)

  useEffect(() => {
    let cancelled = false
    fetchStatusHistory()
      .then((result) => {
        if (!cancelled) {
          setEntries(result)
        }
      })
      .catch(() => {
        if (!cancelled) {
          setChartLoadError(true)
        }
      })

    return () => {
      cancelled = true
    }
  }, [])

  return (
    <main className="flex min-h-svh flex-col gap-4 p-4">
      <div className="flex items-center justify-between">
        <h1 className="text-lg font-bold">{t('dashboard.nav.trendHistory')}</h1>
        <button
          type="button"
          onClick={onSmartPlugImportClick}
          aria-label={t('smartPlugImport.entryPointLabel')}
          title={t('smartPlugImport.entryPointLabel')}
          className="bg-nav-chrome-active-bg text-nav-chrome-active-foreground flex size-10 shrink-0 items-center justify-center rounded-xl"
        >
          <Upload className="size-4" aria-hidden="true" />
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

        <MeterReadingsCard locale={locale} />
      </div>

      <NavChrome active="trendHistory" onDashboardClick={onBack} onTrendHistoryClick={() => {}} onSettingsClick={onSettingsClick} />
    </main>
  )
}
