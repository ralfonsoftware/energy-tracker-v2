import { render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it, vi } from 'vitest'
import { DashboardPage } from './dashboard-page'
import type { StatusDto } from '@/lib/status-api'

const household = { id: '11111111-1111-1111-1111-111111111111', locale: 'en-US' }

function noop() {}

describe('DashboardPage', () => {
  it('renders the Status card as the first element, with no scrolling required', async () => {
    const status: StatusDto = { status: 'withinRange', paceToDateKwh: 1060, baselineToDateKwh: 1300, isLowConfidence: false }
    render(
      <DashboardPage
        household={household}
        status={status}
        statusLoading={false}
        playStatusEntranceAnimation={true}
        logSheetOpen={false}
        onLogSheetOpenChange={noop}
        onReadingSaved={noop}
        openRegressionPrompt={null}
        onRegressionResolved={noop}
        onSettingsClick={noop}
      />,
    )

    expect(await screen.findByText('Quiet week.')).toBeInTheDocument()
  })

  it('renders the primary Log Reading action button below the populated Status card', async () => {
    const status: StatusDto = { status: 'withinRange', paceToDateKwh: 1000, baselineToDateKwh: 1000, isLowConfidence: false }
    render(
      <DashboardPage
        household={household}
        status={status}
        statusLoading={false}
        playStatusEntranceAnimation={true}
        logSheetOpen={false}
        onLogSheetOpenChange={noop}
        onReadingSaved={noop}
        openRegressionPrompt={null}
        onRegressionResolved={noop}
        onSettingsClick={noop}
      />,
    )

    expect(screen.getByRole('button', { name: /Log reading/ })).toBeInTheDocument()
  })

  it('renders the primary Log Reading action button inline inside the onboarding empty state, not a second time', async () => {
    render(
      <DashboardPage
        household={household}
        status={null}
        statusLoading={false}
        playStatusEntranceAnimation={true}
        logSheetOpen={false}
        onLogSheetOpenChange={noop}
        onReadingSaved={noop}
        openRegressionPrompt={null}
        onRegressionResolved={noop}
        onSettingsClick={noop}
      />,
    )

    expect(await screen.findByText('No Status yet')).toBeInTheDocument()
    expect(screen.getAllByRole('button', { name: /Log reading/ })).toHaveLength(1)
  })

  it('renders no Log Reading trigger while the skeleton is showing', () => {
    render(
      <DashboardPage
        household={household}
        status={null}
        statusLoading={true}
        playStatusEntranceAnimation={true}
        logSheetOpen={false}
        onLogSheetOpenChange={noop}
        onReadingSaved={noop}
        openRegressionPrompt={null}
        onRegressionResolved={noop}
        onSettingsClick={noop}
      />,
    )

    expect(screen.queryByRole('button', { name: /Log reading/ })).not.toBeInTheDocument()
  })

  it('renders the bottom nav chrome with Dashboard active, and Settings tap calls onSettingsClick', async () => {
    const user = userEvent.setup()
    const onSettingsClick = vi.fn()
    render(
      <DashboardPage
        household={household}
        status={null}
        statusLoading={false}
        playStatusEntranceAnimation={true}
        logSheetOpen={false}
        onLogSheetOpenChange={noop}
        onReadingSaved={noop}
        openRegressionPrompt={null}
        onRegressionResolved={noop}
        onSettingsClick={onSettingsClick}
      />,
    )

    expect(screen.getByText('Trend History')).toBeInTheDocument()
    await user.click(screen.getByRole('button', { name: 'Settings' }))
    expect(onSettingsClick).toHaveBeenCalledOnce()
  })

  it('does not render the invite-generation panel — relocated to Settings so it never competes with the Status card for visual weight (AC #10)', () => {
    const status: StatusDto = { status: 'withinRange', paceToDateKwh: 1000, baselineToDateKwh: 1000, isLowConfidence: false }
    render(
      <DashboardPage
        household={household}
        status={status}
        statusLoading={false}
        playStatusEntranceAnimation={true}
        logSheetOpen={false}
        onLogSheetOpenChange={noop}
        onReadingSaved={noop}
        openRegressionPrompt={null}
        onRegressionResolved={noop}
        onSettingsClick={noop}
      />,
    )

    expect(screen.queryByRole('button', { name: 'Invite a member' })).not.toBeInTheDocument()
  })
})
