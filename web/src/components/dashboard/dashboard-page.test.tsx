import { render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { DashboardPage } from './dashboard-page'
import type { StatusDto } from '@/lib/status-api'
import type { MeterRegressionPromptDto } from '@/lib/meter-regression-api'

const household = { id: '11111111-1111-1111-1111-111111111111', locale: 'en-US' }

function noop() {}

function regressionPrompt(): MeterRegressionPromptDto {
  return {
    id: '22222222-2222-2222-2222-222222222222',
    meterReadingId: '33333333-3333-3333-3333-333333333333',
    readingKwhValue: 50,
    readingTimestamp: new Date().toISOString(),
    previousMeterReadingId: '44444444-4444-4444-4444-444444444444',
    previousReadingKwhValue: 1000,
    previousReadingTimestamp: new Date().toISOString(),
    mainMeterDigitCapacityKwh: null,
  }
}

describe('DashboardPage', () => {
  afterEach(() => {
    vi.unstubAllGlobals()
  })

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
        onHistoryClick={noop}
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
        onHistoryClick={noop}
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
        onHistoryClick={noop}
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
        onHistoryClick={noop}
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
        onHistoryClick={noop}
      />,
    )

    expect(screen.getByText('Trend History')).toBeInTheDocument()
    await user.click(screen.getByRole('button', { name: 'Settings' }))
    expect(onSettingsClick).toHaveBeenCalledOnce()
  })

  it('renders the history trigger only when populated, and calls onHistoryClick when clicked', async () => {
    const user = userEvent.setup()
    const onHistoryClick = vi.fn()
    const status: StatusDto = { status: 'withinRange', paceToDateKwh: 1000, baselineToDateKwh: 1000, isLowConfidence: false }
    const { rerender } = render(
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
        onHistoryClick={onHistoryClick}
      />,
    )

    expect(screen.queryByRole('button', { name: 'View reading history' })).not.toBeInTheDocument()

    rerender(
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
        onHistoryClick={onHistoryClick}
      />,
    )

    const trigger = await screen.findByRole('button', { name: 'View reading history' })
    await user.click(trigger)
    expect(onHistoryClick).toHaveBeenCalledOnce()
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
        onHistoryClick={noop}
      />,
    )

    expect(screen.queryByRole('button', { name: 'Invite a member' })).not.toBeInTheDocument()
  })

  it('closes the Status detail dialog when openRegressionPrompt transitions from null to non-null while it is open', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn(() =>
        Promise.resolve(
          new Response(
            JSON.stringify({
              status: 'withinRange',
              paceToDateKwh: 1060,
              baselineToDateKwh: 1300,
              elapsedDays: 182.5,
              trendingThresholdKwh: 100,
              isLowConfidence: false,
              daysSinceLastReading: 1,
              lowConfidenceGapDaysThreshold: 45,
            }),
          ),
        ),
      ),
    )
    const user = userEvent.setup()
    const status: StatusDto = { status: 'withinRange', paceToDateKwh: 1060, baselineToDateKwh: 1300, isLowConfidence: false }
    const { rerender } = render(
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
        onHistoryClick={noop}
      />,
    )

    await user.click(screen.getByRole('button', { name: 'How was this calculated?' }))
    expect(await screen.findByText('Status calculation')).toBeInTheDocument()

    rerender(
      <DashboardPage
        household={household}
        status={status}
        statusLoading={false}
        playStatusEntranceAnimation={true}
        logSheetOpen={false}
        onLogSheetOpenChange={noop}
        onReadingSaved={noop}
        openRegressionPrompt={regressionPrompt()}
        onRegressionResolved={noop}
        onSettingsClick={noop}
        onHistoryClick={noop}
      />,
    )

    await waitFor(() => expect(screen.queryByText('Status calculation')).not.toBeInTheDocument())
  })

  it('closes the Status detail dialog when status transitions to null while it is open, and does not silently reopen once status repopulates', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn(() =>
        Promise.resolve(
          new Response(
            JSON.stringify({
              status: 'withinRange',
              paceToDateKwh: 1060,
              baselineToDateKwh: 1300,
              elapsedDays: 182.5,
              trendingThresholdKwh: 100,
              isLowConfidence: false,
              daysSinceLastReading: 1,
              lowConfidenceGapDaysThreshold: 45,
            }),
          ),
        ),
      ),
    )
    const user = userEvent.setup()
    const status: StatusDto = { status: 'withinRange', paceToDateKwh: 1060, baselineToDateKwh: 1300, isLowConfidence: false }
    const { rerender } = render(
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
        onHistoryClick={noop}
      />,
    )

    await user.click(screen.getByRole('button', { name: 'How was this calculated?' }))
    expect(await screen.findByText('Status calculation')).toBeInTheDocument()

    // A transient status refresh failure (background sync, etc.) drops status to null.
    rerender(
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
        onHistoryClick={noop}
      />,
    )

    await waitFor(() => expect(screen.queryByText('Status calculation')).not.toBeInTheDocument())

    // Status repopulates — the dialog must not silently reopen on its own.
    rerender(
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
        onHistoryClick={noop}
      />,
    )

    expect(screen.queryByText('Status calculation')).not.toBeInTheDocument()
  })
})
