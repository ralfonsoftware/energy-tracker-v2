import { Home, LineChart, Clock, Settings as SettingsIcon } from 'lucide-react'
import { useTranslation } from 'react-i18next'
import { cn } from '@/lib/utils'
import { ProfileMenu } from './profile-menu'

type NavTab = 'dashboard' | 'trendHistory' | 'tariffRadar' | 'settings'

interface NavChromeProps {
  active: NavTab
  onDashboardClick: () => void
  onTrendHistoryClick: () => void
  onTariffRadarClick: () => void
  onSettingsClick: () => void
  householdId: string
  supportsFederatedLogout: boolean
  email: string | null
}

const ITEM_CLASSNAME =
  'flex min-w-14 flex-col items-center gap-1 rounded-2xl px-2.5 py-1.5 text-muted-foreground'
const ACTIVE_CLASSNAME = 'bg-nav-chrome-active-bg text-nav-chrome-active-foreground'

const LINK_CLASSNAME =
  'flex items-center gap-1.5 rounded-xl px-3 py-2 text-xs font-semibold text-muted-foreground'

// One component, not two (UX-DR9/DESIGN/components.md) — the exact same active/onXClick props
// render both a <660px bottom-tab-bar and a >=660px top-nav variant, swapped via the `wide:`
// breakpoint variant (Task 1) rather than two separate components. NavChrome's own mount point in
// every page stays exactly where it already is; `wide:order-first` alone repositions the wide
// variant to visual top (Task 2) — see nav-chrome's wrapping <div> below.
export function NavChrome({
  active,
  onDashboardClick,
  onTrendHistoryClick,
  onTariffRadarClick,
  onSettingsClick,
  householdId,
  supportsFederatedLogout,
  email,
}: NavChromeProps) {
  const { t } = useTranslation()

  return (
    // mt-auto is what pushed NavChrome to the bottom of each page's flex-col <main> before this
    // story — preserved here on the wrapper (now the actual flex item) for <660px. At >=660px,
    // wide:order-first repositions this same single mount point to visual top (Task 2) — mt-0
    // cancels the auto margin so it doesn't fight the reordering.
    <div className="mt-auto wide:order-first wide:mt-0">
      {/* Bottom tab bar — mobile convention, unchanged below the 660px breakpoint (AC #2).
          data-slot is a stable, visibility-independent hook for e2e viewport-resize assertions
          (Task 7) — Playwright's role queries exclude display:none elements from the a11y tree,
          so a plain role/text locator can't reliably tell the two <nav>s apart across a resize. */}
      <nav data-slot="nav-chrome-bottom" className="wide:hidden flex items-stretch justify-around border-t border-border px-2 pt-2.5 pb-4">
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

      {/* Top nav — desktop/tablet convention at >=660px (AC #1). Brand wordmark, the same four
          links, then the Profile menu (Task 3) on the far right; this is the only surface that
          mounts ProfileMenu (no <660px equivalent). */}
      <nav data-slot="nav-chrome-top" className="hidden wide:flex items-center justify-between border-b border-border px-4 py-2.5">
        <span className="text-sm font-bold">{t('app.title')}</span>

        <div className="flex items-center gap-1">
          <button
            type="button"
            className={cn(LINK_CLASSNAME, active === 'dashboard' && ACTIVE_CLASSNAME)}
            aria-current={active === 'dashboard' ? 'page' : undefined}
            onClick={onDashboardClick}
          >
            <Home className="size-4" aria-hidden="true" />
            {t('dashboard.nav.dashboard')}
          </button>

          <button
            type="button"
            className={cn(LINK_CLASSNAME, active === 'trendHistory' && ACTIVE_CLASSNAME)}
            aria-current={active === 'trendHistory' ? 'page' : undefined}
            onClick={onTrendHistoryClick}
          >
            <LineChart className="size-4" aria-hidden="true" />
            {t('dashboard.nav.trendHistory')}
          </button>

          <button
            type="button"
            className={cn(LINK_CLASSNAME, active === 'tariffRadar' && ACTIVE_CLASSNAME)}
            aria-current={active === 'tariffRadar' ? 'page' : undefined}
            onClick={onTariffRadarClick}
          >
            <Clock className="size-4" aria-hidden="true" />
            {t('dashboard.nav.tariffRadar')}
          </button>

          <button
            type="button"
            className={cn(LINK_CLASSNAME, active === 'settings' && ACTIVE_CLASSNAME)}
            aria-current={active === 'settings' ? 'page' : undefined}
            onClick={onSettingsClick}
          >
            <SettingsIcon className="size-4" aria-hidden="true" />
            {t('dashboard.nav.settings')}
          </button>
        </div>

        <ProfileMenu email={email} householdId={householdId} supportsFederatedLogout={supportsFederatedLogout} />
      </nav>
    </div>
  )
}
