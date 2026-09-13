import { useState } from 'react'
import { useTranslation } from 'react-i18next'
import { NavChrome } from '@/components/dashboard/nav-chrome'
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

  return (
    <main className="flex min-h-svh flex-col gap-4 p-4">
      <h1 className="text-lg font-bold">{t('dashboard.nav.tariffRadar')}</h1>

      <div className="flex flex-col gap-[var(--spacing-card-gap)]">
        <TariffConfigurationForm
          householdCurrency={householdCurrency}
          onCreated={() => setRefreshNonce((n) => n + 1)}
        />

        <TariffHistoryList locale={locale} refreshNonce={refreshNonce} />
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
