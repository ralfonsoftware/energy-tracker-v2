import { useCallback, useState } from 'react'
import { useTranslation } from 'react-i18next'
import { NavChrome } from '@/components/dashboard/nav-chrome'
import type { TariffHistoryPageDto } from '@/lib/tariff-api'
import { TariffComparisonForm } from './tariff-comparison-form'
import { TariffConfigurationForm } from './tariff-configuration-form'
import { TariffHistoryList } from './tariff-history-list'

interface TariffRadarPageProps {
  locale: string
  householdCurrency: string
  onBack: () => void
  onTrendHistoryClick: () => void
  onSettingsClick: () => void
}

// Shell mirrors TrendHistoryPage/SettingsPage — Tariff Radar's first real content behind a
// previously-inert placeholder nav tab (nav-chrome.tsx's own comment named this exact story).
export function TariffRadarPage({ locale, householdCurrency, onBack, onTrendHistoryClick, onSettingsClick }: TariffRadarPageProps) {
  const { t } = useTranslation()
  // Bumped after a create/edit to make TariffHistoryList re-fetch — simpler than lifting its
  // fetch state up, mirrors the "onSaved triggers the sibling list's own reload" pattern
  // MeterReadingsCard/EditMeterReadingDialog already establish, just via a nonce instead of a
  // passed-down callback since Create and Edit are two different child components here.
  const [refreshNonce, setRefreshNonce] = useState(0)
  // Frontend gating recommendation (Story 5.2 Task 3): only render the comparison form once the
  // household has a *current* Tariff entry (not merely any entry — a future-dated-only entry
  // would still hit the backend's "no current Tariff" null case) — avoids ever exercising that
  // edge case in normal use. Also carries the current Tariff's own Currency (independent from
  // Household.Currency, Tariff.cs's own doc comment) through to the comparison form's candidate
  // field labels, so they never show a different currency unit than the result they're for.
  // Reuses TariffHistoryList's own already-fetched page instead of a second duplicate HTTP round
  // trip.
  const [currentTariffCurrency, setCurrentTariffCurrency] = useState<string | null>(null)
  // Stable identity (empty deps) — a fresh callback every render would give TariffHistoryList's
  // own `load` a new identity each time too, re-triggering its fetch effect in a loop (Story
  // 3.5's own review-found render-loop precedent).
  const handleHistoryLoaded = useCallback((page: TariffHistoryPageDto) => {
    setCurrentTariffCurrency(page.items.find((item) => item.isCurrent)?.currency ?? null)
  }, [])

  return (
    <main className="flex min-h-svh flex-col gap-4 p-4">
      <h1 className="text-lg font-bold">{t('dashboard.nav.tariffRadar')}</h1>

      <div className="flex flex-col gap-[var(--spacing-card-gap)]">
        <TariffConfigurationForm
          householdCurrency={householdCurrency}
          onCreated={() => setRefreshNonce((n) => n + 1)}
        />

        <TariffHistoryList locale={locale} refreshNonce={refreshNonce} onLoaded={handleHistoryLoaded} />

        {currentTariffCurrency && <TariffComparisonForm currency={currentTariffCurrency} locale={locale} />}
      </div>

      <NavChrome
        active="tariffRadar"
        onDashboardClick={onBack}
        onTrendHistoryClick={onTrendHistoryClick}
        onSettingsClick={onSettingsClick}
        onTariffRadarClick={() => {}}
      />
    </main>
  )
}
