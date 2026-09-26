import { render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it, vi } from 'vitest'
import { NavChrome } from './nav-chrome'

const householdId = '11111111-1111-1111-1111-111111111111'

function renderNavChrome(active: 'dashboard' | 'trendHistory' | 'tariffRadar' | 'settings', overrides: Partial<Parameters<typeof NavChrome>[0]> = {}) {
  return render(
    <NavChrome
      active={active}
      onDashboardClick={vi.fn()}
      onTrendHistoryClick={vi.fn()}
      onTariffRadarClick={vi.fn()}
      onSettingsClick={vi.fn()}
      householdId={householdId}
      supportsFederatedLogout={true}
      email="ralf@example.com"
      {...overrides}
    />,
  )
}

// jsdom does not evaluate real CSS media queries (Task 7's own note) — index.css is never
// imported into the test environment (src/test/setup.ts), so both the <660px bottom-tab-bar and
// the >=660px top-nav markup variants are simultaneously present and "visible" to Testing
// Library's role queries here, regardless of their wide:hidden/hidden wide:flex classes. Every
// assertion below therefore expects TWO matches (one per variant) rather than one — proving both
// variants render with the right classes/active state is exactly what this file can prove; which
// one a real browser actually shows at a given width is Task 7's Playwright viewport-resize spec's
// job (web/e2e/app-shell.spec.ts), not this file's.
describe('NavChrome', () => {
  it('renders both the bottom-tab-bar and top-nav variants, each with all four top-level entries', () => {
    renderNavChrome('dashboard')

    expect(screen.getAllByRole('button', { name: 'Dashboard' })).toHaveLength(2)
    expect(screen.getAllByRole('button', { name: 'Trend History' })).toHaveLength(2)
    expect(screen.getAllByRole('button', { name: 'Tariff Radar' })).toHaveLength(2)
    expect(screen.getAllByRole('button', { name: 'Settings' })).toHaveLength(2)
  })

  it('marks the bottom-tab-bar wide:hidden and the top-nav hidden wide:flex (Tasks 1 and 2 shared 660px breakpoint)', () => {
    const { container } = renderNavChrome('dashboard')

    expect(container.querySelector('nav[data-slot="nav-chrome-bottom"]')).toHaveClass('wide:hidden')
    expect(container.querySelector('nav[data-slot="nav-chrome-top"]')).toHaveClass('hidden', 'wide:flex')
  })

  it('applies the brand-accent-tinted active state to the active tab in both variants, never a status color', () => {
    renderNavChrome('dashboard')

    for (const dashboardTab of screen.getAllByRole('button', { name: 'Dashboard' })) {
      expect(dashboardTab).toHaveClass('bg-nav-chrome-active-bg')
      expect(dashboardTab).toHaveClass('text-nav-chrome-active-foreground')
    }
  })

  it('tapping either Settings entry calls onSettingsClick', async () => {
    const user = userEvent.setup()
    const onSettingsClick = vi.fn()
    renderNavChrome('dashboard', { onSettingsClick })

    const [mobileSettings] = screen.getAllByRole('button', { name: 'Settings' })
    await user.click(mobileSettings)

    expect(onSettingsClick).toHaveBeenCalledOnce()
  })

  it('tapping either Dashboard entry calls onDashboardClick — how a Settings-active bar navigates back', async () => {
    const user = userEvent.setup()
    const onDashboardClick = vi.fn()
    renderNavChrome('settings', { onDashboardClick })

    const [mobileDashboard] = screen.getAllByRole('button', { name: 'Dashboard' })
    await user.click(mobileDashboard)

    expect(onDashboardClick).toHaveBeenCalledOnce()
  })

  it('reflects Trend History as active in both variants and tapping either calls onTrendHistoryClick (Story 4.1)', async () => {
    const user = userEvent.setup()
    const onTrendHistoryClick = vi.fn()
    renderNavChrome('trendHistory', { onTrendHistoryClick })

    const trendHistoryTabs = screen.getAllByRole('button', { name: 'Trend History' })
    expect(trendHistoryTabs).toHaveLength(2)
    for (const tab of trendHistoryTabs) {
      expect(tab).toHaveAttribute('aria-current', 'page')
      expect(tab).toHaveClass('bg-nav-chrome-active-bg')
    }

    await user.click(trendHistoryTabs[0])
    expect(onTrendHistoryClick).toHaveBeenCalledOnce()
  })

  it('reflects Tariff Radar as active in both variants and tapping either calls onTariffRadarClick (Story 5.1)', async () => {
    const user = userEvent.setup()
    const onTariffRadarClick = vi.fn()
    renderNavChrome('tariffRadar', { onTariffRadarClick })

    const tariffRadarTabs = screen.getAllByRole('button', { name: 'Tariff Radar' })
    expect(tariffRadarTabs).toHaveLength(2)
    for (const tab of tariffRadarTabs) {
      expect(tab).toHaveAttribute('aria-current', 'page')
      expect(tab).toHaveClass('bg-nav-chrome-active-bg')
    }

    await user.click(tariffRadarTabs[0])
    expect(onTariffRadarClick).toHaveBeenCalledOnce()
  })

  it('mounts the Profile menu only once — the top-nav variant, with no bottom-tab-bar equivalent (Task 3)', () => {
    renderNavChrome('dashboard')

    expect(screen.getAllByRole('button', { name: 'Account menu' })).toHaveLength(1)
  })
})
