import { render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it, vi } from 'vitest'
import { NavChrome } from './nav-chrome'

describe('NavChrome', () => {
  it('renders all four top-level entries', () => {
    render(<NavChrome active="dashboard" onDashboardClick={vi.fn()} onTrendHistoryClick={vi.fn()} onSettingsClick={vi.fn()} />)

    expect(screen.getByText('Dashboard')).toBeInTheDocument()
    expect(screen.getByText('Trend History')).toBeInTheDocument()
    expect(screen.getByText('Tariff Radar')).toBeInTheDocument()
    expect(screen.getByText('Settings')).toBeInTheDocument()
  })

  it('applies the brand-accent-tinted active state to the active tab, never a status color', () => {
    render(<NavChrome active="dashboard" onDashboardClick={vi.fn()} onTrendHistoryClick={vi.fn()} onSettingsClick={vi.fn()} />)

    const dashboardTab = screen.getByRole('button', { name: 'Dashboard' })
    expect(dashboardTab).toHaveClass('bg-nav-chrome-active-bg')
    expect(dashboardTab).toHaveClass('text-nav-chrome-active-foreground')
  })

  it('tapping Settings calls onSettingsClick', async () => {
    const user = userEvent.setup()
    const onSettingsClick = vi.fn()
    render(<NavChrome active="dashboard" onDashboardClick={vi.fn()} onTrendHistoryClick={vi.fn()} onSettingsClick={onSettingsClick} />)

    await user.click(screen.getByRole('button', { name: 'Settings' }))

    expect(onSettingsClick).toHaveBeenCalledOnce()
  })

  it('tapping Dashboard calls onDashboardClick — how a Settings-active bar navigates back', async () => {
    const user = userEvent.setup()
    const onDashboardClick = vi.fn()
    render(<NavChrome active="settings" onDashboardClick={onDashboardClick} onTrendHistoryClick={vi.fn()} onSettingsClick={vi.fn()} />)

    await user.click(screen.getByRole('button', { name: 'Dashboard' }))

    expect(onDashboardClick).toHaveBeenCalledOnce()
  })

  it('tapping Trend History calls onTrendHistoryClick and reflects the active state (Story 4.1)', async () => {
    const user = userEvent.setup()
    const onTrendHistoryClick = vi.fn()
    render(<NavChrome active="trendHistory" onDashboardClick={vi.fn()} onTrendHistoryClick={onTrendHistoryClick} onSettingsClick={vi.fn()} />)

    const trendHistoryTab = screen.getByRole('button', { name: 'Trend History' })
    expect(trendHistoryTab).toHaveAttribute('aria-current', 'page')
    expect(trendHistoryTab).toHaveClass('bg-nav-chrome-active-bg')

    await user.click(trendHistoryTab)

    expect(onTrendHistoryClick).toHaveBeenCalledOnce()
  })

  it('the Tariff Radar tab is inert — no click handler, aria-disabled', async () => {
    const user = userEvent.setup()
    render(<NavChrome active="dashboard" onDashboardClick={vi.fn()} onTrendHistoryClick={vi.fn()} onSettingsClick={vi.fn()} />)

    const tariffTab = screen.getByText('Tariff Radar').closest('[role="button"], button')
    expect(tariffTab).toHaveAttribute('aria-disabled', 'true')

    // Clicking must not throw and must not navigate anywhere — nothing to assert on besides
    // "did not crash", since there's no onClick prop for this tab at all.
    if (tariffTab) {
      await user.click(tariffTab)
    }
  })
})
