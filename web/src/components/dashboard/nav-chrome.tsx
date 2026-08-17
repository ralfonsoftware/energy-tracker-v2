import { Home, LineChart, Clock, Settings as SettingsIcon } from 'lucide-react'
import { useTranslation } from 'react-i18next'
import { cn } from '@/lib/utils'

type NavTab = 'dashboard' | 'trendHistory' | 'tariffRadar' | 'settings'

interface NavChromeProps {
  active: NavTab
  onDashboardClick: () => void
  onSettingsClick: () => void
}

const ITEM_CLASSNAME =
  'flex min-w-14 flex-col items-center gap-1 rounded-2xl px-2.5 py-1.5 text-muted-foreground'
const ACTIVE_CLASSNAME = 'bg-nav-chrome-active-bg text-nav-chrome-active-foreground'

// The bottom tab bar shell — all four top-level entries per UX-DR9. Only Dashboard (this
// surface) and Settings (Story 1.9, already built) are interactive; Trend History and Tariff
// Radar don't have a surface yet (Epic 4/Epic 5) and render as inert placeholders rather than a
// tap that goes nowhere. Confirmed with Ralf during dev-story activation.
export function NavChrome({ active, onDashboardClick, onSettingsClick }: NavChromeProps) {
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

      <div className={ITEM_CLASSNAME} role="button" aria-disabled="true">
        <LineChart className="size-5" aria-hidden="true" />
        <span className="text-[9.5px] font-semibold">{t('dashboard.nav.trendHistory')}</span>
      </div>

      <div className={ITEM_CLASSNAME} role="button" aria-disabled="true">
        <Clock className="size-5" aria-hidden="true" />
        <span className="text-[9.5px] font-semibold">{t('dashboard.nav.tariffRadar')}</span>
      </div>

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
