import { render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it, vi } from 'vitest'
import { NavChrome } from './nav-chrome'

describe('NavChrome', () => {
  it('renders all four top-level entries', () => {
    render(
      <NavChrome
        active="dashboard"
        onDashboardClick={vi.fn()}
        onTrendHistoryClick={vi.fn()}
        onTariffRadarClick={vi.fn()}
        onSettingsClick={vi.fn()}
      />,
    )

    expect(screen.getByText('Dashboard')).toBeInTheDocument()
    expect(screen.getByText('Trend History')).toBeInTheDocument()
    expect(screen.getByText('Tariff Radar')).toBeInTheDocument()
    expect(screen.getByText('Settings')).toBeInTheDocument()
  })

  it('applies the brand-accent-tinted active state to the active tab, never a status color', () => {
    render(
      <NavChrome
        active="dashboard"
        onDashboardClick={vi.fn()}
        onTrendHistoryClick={vi.fn()}
        onTariffRadarClick={vi.fn()}
        onSettingsClick={vi.fn()}
      />,
    )

    const dashboardTab = screen.getByRole('button', { name: 'Dashboard' })
    expect(dashboardTab).toHaveClass('bg-nav-chrome-active-bg')
    expect(dashboardTab).toHaveClass('text-nav-chrome-active-foreground')
  })

  it('tapping Settings calls onSettingsClick', async () => {
    const user = userEvent.setup()
    const onSettingsClick = vi.fn()
    render(
      <NavChrome
        active="dashboard"
        onDashboardClick={vi.fn()}
        onTrendHistoryClick={vi.fn()}
        onTariffRadarClick={vi.fn()}
        onSettingsClick={onSettingsClick}
      />,
    )

    await user.click(screen.getByRole('button', { name: 'Settings' }))

    expect(onSettingsClick).toHaveBeenCalledOnce()
  })

  it('tapping Dashboard calls onDashboardClick — how a Settings-active bar navigates back', async () => {
    const user = userEvent.setup()
    const onDashboardClick = vi.fn()
    render(
      <NavChrome
        active="settings"
        onDashboardClick={onDashboardClick}
        onTrendHistoryClick={vi.fn()}
        onTariffRadarClick={vi.fn()}
        onSettingsClick={vi.fn()}
      />,
    )

    await user.click(screen.getByRole('button', { name: 'Dashboard' }))

    expect(onDashboardClick).toHaveBeenCalledOnce()
  })

  it('tapping Trend History calls onTrendHistoryClick and reflects the active state (Story 4.1)', async () => {
    const user = userEvent.setup()
    const onTrendHistoryClick = vi.fn()
    render(
      <NavChrome
        active="trendHistory"
        onDashboardClick={vi.fn()}
        onTrendHistoryClick={onTrendHistoryClick}
        onTariffRadarClick={vi.fn()}
        onSettingsClick={vi.fn()}
      />,
    )

    const trendHistoryTab = screen.getByRole('button', { name: 'Trend History' })
    expect(trendHistoryTab).toHaveAttribute('aria-current', 'page')
    expect(trendHistoryTab).toHaveClass('bg-nav-chrome-active-bg')

    await user.click(trendHistoryTab)

    expect(onTrendHistoryClick).toHaveBeenCalledOnce()
  })

  it('tapping Tariff Radar calls onTariffRadarClick and reflects the active state (Story 5.1)', async () => {
    const user = userEvent.setup()
    const onTariffRadarClick = vi.fn()
    render(
      <NavChrome
        active="tariffRadar"
        onDashboardClick={vi.fn()}
        onTrendHistoryClick={vi.fn()}
        onTariffRadarClick={onTariffRadarClick}
        onSettingsClick={vi.fn()}
      />,
    )

    const tariffRadarTab = screen.getByRole('button', { name: 'Tariff Radar' })
    expect(tariffRadarTab).toHaveAttribute('aria-current', 'page')
    expect(tariffRadarTab).toHaveClass('bg-nav-chrome-active-bg')

    await user.click(tariffRadarTab)

    expect(onTariffRadarClick).toHaveBeenCalledOnce()
  })
})
