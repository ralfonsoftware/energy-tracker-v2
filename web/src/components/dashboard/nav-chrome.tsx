import { Home, LineChart, Clock, Settings as SettingsIcon } from 'lucide-react'
import { useTranslation } from 'react-i18next'
import { cn } from '@/lib/utils'

type NavTab = 'dashboard' | 'trendHistory' | 'tariffRadar' | 'settings'

interface NavChromeProps {
  active: NavTab
  onDashboardClick: () => void
  onTrendHistoryClick: () => void
  onTariffRadarClick: () => void
  onSettingsClick: () => void
}

const ITEM_CLASSNAME =
  'flex min-w-14 flex-col items-center gap-1 rounded-2xl px-2.5 py-1.5 text-muted-foreground'
const ACTIVE_CLASSNAME = 'bg-nav-chrome-active-bg text-nav-chrome-active-foreground'

// The bottom tab bar shell — all four top-level entries per UX-DR9. All four are now interactive
// (Story 5.1 gives Tariff Radar its real surface, closing out the last inert placeholder tab).
export function NavChrome({ active, onDashboardClick, onTrendHistoryClick, onTariffRadarClick, onSettingsClick }: NavChromeProps) {
  const { t } = useTranslation()

  return (
    <nav className="mt-auto flex items-stretch justify-around border-t border-border px-2 pt-2.5 pb-4">
      <button
        type="button"
        className={cn(ITEM_CLASSNAME, active === 'dashboard' && ACTIVE_CLASSNAME)}
        aria-current={active === 'dashboard' ? 'page' : undefined}
        onClick={onDashboardClick}
      >
        <Home className="size-5" aria-hidden="true" />
        <span className="text-[9.5px] font-semibold">{t('dashboard.nav.dashboard')}</span>
      </button>

      <button
        type="button"
        className={cn(ITEM_CLASSNAME, active === 'trendHistory' && ACTIVE_CLASSNAME)}
        aria-current={active === 'trendHistory' ? 'page' : undefined}
        onClick={onTrendHistoryClick}
      >
        <LineChart className="size-5" aria-hidden="true" />
        <span className="text-[9.5px] font-semibold">{t('dashboard.nav.trendHistory')}</span>
      </button>

      <button
        type="button"
        className={cn(ITEM_CLASSNAME, active === 'tariffRadar' && ACTIVE_CLASSNAME)}
        aria-current={active === 'tariffRadar' ? 'page' : undefined}
        onClick={onTariffRadarClick}
      >
        <Clock className="size-5" aria-hidden="true" />
        <span className="text-[9.5px] font-semibold">{t('dashboard.nav.tariffRadar')}</span>
      </button>

      <button
        type="button"
        className={cn(ITEM_CLASSNAME, active === 'settings' && ACTIVE_CLASSNAME)}
        aria-current={active === 'settings' ? 'page' : undefined}
        onClick={onSettingsClick}
      >
        <SettingsIcon className="size-5" aria-hidden="true" />
        <span className="text-[9.5px] font-semibold">{t('dashboard.nav.settings')}</span>
      </button>
    </nav>
  )
}
