import { render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it, vi } from 'vitest'
import { NavChrome } from './nav-chrome'

const householdId = '11111111-1111-1111-1111-111111111111'

type Placement = 'top' | 'bottom'

function renderNavChrome(
  placement: Placement,
  active: 'dashboard' | 'trendHistory' | 'tariffRadar' | 'settings',
  overrides: Partial<Parameters<typeof NavChrome>[0]> = {},
) {
  return render(
    <NavChrome
      placement={placement}
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

// jsdom does not evaluate real CSS media queries — index.css is never imported into the test
// environment (src/test/setup.ts). Since Story 10.3 each NavChrome mount renders exactly ONE
// variant (placement="top" or "bottom"), so these tests render one at a time and assert one
// match. Which variant a real browser shows at a given width, and where it sits in Tab order, is
// the job of web/e2e/app-shell.spec.ts and web/e2e/tab-order.spec.ts.
const ENTRIES = ['Dashboard', 'Trend History', 'Tariff Radar', 'Settings'] as const

describe('NavChrome skip link (WCAG 2.4.1)', () => {
  it('is the first element of the top nav and points at the content wrapper', () => {
    const { container } = renderNavChrome('top', 'dashboard')

    const nav = container.querySelector('nav[data-slot="nav-chrome-top"]')!
    const link = screen.getByRole('link', { name: 'Skip to main content' })
    expect(nav.firstElementChild).toBe(link)
    expect(link).toHaveAttribute('href', '#main-content')
  })

  it('moves focus to #main-content when activated', async () => {
    const target = document.createElement('div')
    target.id = 'main-content'
    target.tabIndex = -1
    document.body.appendChild(target)
    renderNavChrome('top', 'dashboard')

    await userEvent.click(screen.getByRole('link', { name: 'Skip to main content' }))

    expect(target).toHaveFocus()
    target.remove()
  })

  it('is not rendered in the bottom bar', () => {
    renderNavChrome('bottom', 'dashboard')

    expect(screen.queryByRole('link', { name: 'Skip to main content' })).not.toBeInTheDocument()
  })
})

describe.each<Placement>(['top', 'bottom'])('NavChrome placement="%s"', (placement) => {
  const otherPlacement: Placement = placement === 'top' ? 'bottom' : 'top'

  it('renders exactly one nav for its own placement, with all four top-level entries', () => {
    const { container } = renderNavChrome(placement, 'dashboard')

    expect(container.querySelectorAll('nav[data-slot]')).toHaveLength(1)
    expect(container.querySelector(`nav[data-slot="nav-chrome-${placement}"]`)).toBeInTheDocument()
    expect(container.querySelector(`nav[data-slot="nav-chrome-${otherPlacement}"]`)).not.toBeInTheDocument()
    for (const name of ENTRIES) {
      expect(screen.getAllByRole('button', { name })).toHaveLength(1)
    }
  })

  it('applies the brand-accent-tinted active state and aria-current to the active tab only, never a status color', () => {
    renderNavChrome(placement, 'dashboard')

    const dashboardTab = screen.getByRole('button', { name: 'Dashboard' })
    expect(dashboardTab).toHaveAttribute('aria-current', 'page')
    expect(dashboardTab).toHaveClass('bg-nav-chrome-active-bg')
    expect(dashboardTab).toHaveClass('text-nav-chrome-active-foreground')
    expect(screen.getByRole('button', { name: 'Settings' })).not.toHaveAttribute('aria-current')
  })

  it.each([
    ['Dashboard', 'onDashboardClick', 'settings'],
    ['Trend History', 'onTrendHistoryClick', 'dashboard'],
    ['Tariff Radar', 'onTariffRadarClick', 'dashboard'],
    ['Settings', 'onSettingsClick', 'dashboard'],
  ] as const)('tapping %s calls %s', async (name, handlerName, active) => {
    const user = userEvent.setup()
    const handler = vi.fn()
    renderNavChrome(placement, active, { [handlerName]: handler })

    await user.click(screen.getByRole('button', { name }))

    expect(handler).toHaveBeenCalledOnce()
  })

  it.each([
    ['trendHistory', 'Trend History'],
    ['tariffRadar', 'Tariff Radar'],
    ['settings', 'Settings'],
  ] as const)('reflects %s as active', (active, name) => {
    renderNavChrome(placement, active)

    const tab = screen.getByRole('button', { name })
    expect(tab).toHaveAttribute('aria-current', 'page')
    expect(tab).toHaveClass('bg-nav-chrome-active-bg')
  })
})

describe('NavChrome placement classes (Story 10.3: no wrapper, no CSS order)', () => {
  it('top: hidden wide:flex, with no mt-auto and no wide:order-first', () => {
    const { container } = renderNavChrome('top', 'dashboard')
    const nav = container.querySelector('nav[data-slot="nav-chrome-top"]')

    expect(nav).toHaveClass('hidden', 'wide:flex')
    expect(nav).not.toHaveClass('mt-auto')
    expect(container.innerHTML).not.toContain('wide:order-first')
  })

  it('bottom: mt-auto wide:hidden, and is the root element (no wrapper div that would become an empty flex item)', () => {
    const { container } = renderNavChrome('bottom', 'dashboard')
    const nav = container.querySelector('nav[data-slot="nav-chrome-bottom"]')

    expect(nav).toHaveClass('mt-auto', 'wide:hidden')
    expect(container.firstElementChild).toBe(nav)
  })

  it('top is the root element too', () => {
    const { container } = renderNavChrome('top', 'dashboard')

    expect(container.firstElementChild).toBe(container.querySelector('nav[data-slot="nav-chrome-top"]'))
  })
})

describe('NavChrome Account menu', () => {
  it('mounts the Profile menu in the top placement only (Story 8.1 Task 3)', () => {
    renderNavChrome('top', 'dashboard')
    expect(screen.getAllByRole('button', { name: 'Account menu' })).toHaveLength(1)
  })

  it('does not mount the Profile menu in the bottom placement', () => {
    renderNavChrome('bottom', 'dashboard')
    expect(screen.queryByRole('button', { name: 'Account menu' })).not.toBeInTheDocument()
  })
})
