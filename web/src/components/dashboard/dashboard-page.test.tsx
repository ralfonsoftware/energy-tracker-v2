import { act, render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import 'fake-indexeddb/auto'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { IDBFactory } from 'fake-indexeddb'
import { enqueue, listNeedsAttention, markRejected } from '@/lib/offline-queue'
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
  beforeEach(() => {
    globalThis.indexedDB = new IDBFactory()
  })

  afterEach(() => {
    vi.unstubAllGlobals()
  })

  it('renders the Status card as the first element, with no scrolling required', async () => {
    const status: StatusDto = { status: 'withinRange', paceToDateKwh: 1060, baselineToDateKwh: 1300, isLowConfidence: false }
    render(
      <DashboardPage
        household={household}
        supportsFederatedLogout={true}
        email={null}
        status={status}
        statusLoading={false}
        tariffCheck={null}
        playStatusEntranceAnimation={true}
        logSheetOpen={false}
        onLogSheetOpenChange={noop}
        logEventOpen={false}
        onLogEventOpenChange={noop}
        onReadingSaved={noop}
        openRegressionPrompt={null}
        onRegressionResolved={noop}
        onSettingsClick={noop}
        onTrendHistoryClick={noop}
        onTariffRadarClick={noop}
        onSmartPlugImportClick={noop}
      />,
    )

    expect(await screen.findByText('Quiet week.')).toBeInTheDocument()
  })

  it('renders the primary Log Reading action button below the populated Status card', async () => {
    const status: StatusDto = { status: 'withinRange', paceToDateKwh: 1000, baselineToDateKwh: 1000, isLowConfidence: false }
    render(
      <DashboardPage
        household={household}
        supportsFederatedLogout={true}
        email={null}
        status={status}
        statusLoading={false}
        tariffCheck={null}
        playStatusEntranceAnimation={true}
        logSheetOpen={false}
        onLogSheetOpenChange={noop}
        logEventOpen={false}
        onLogEventOpenChange={noop}
        onReadingSaved={noop}
        openRegressionPrompt={null}
        onRegressionResolved={noop}
        onSettingsClick={noop}
        onTrendHistoryClick={noop}
        onTariffRadarClick={noop}
        onSmartPlugImportClick={noop}
      />,
    )

    expect(screen.getByRole('button', { name: /Log reading/ })).toBeInTheDocument()
  })

  it('renders the primary Log Reading action button inline inside the onboarding empty state, not a second time', async () => {
    render(
      <DashboardPage
        household={household}
        supportsFederatedLogout={true}
        email={null}
        status={null}
        statusLoading={false}
        tariffCheck={null}
        playStatusEntranceAnimation={true}
        logSheetOpen={false}
        onLogSheetOpenChange={noop}
        logEventOpen={false}
        onLogEventOpenChange={noop}
        onReadingSaved={noop}
        openRegressionPrompt={null}
        onRegressionResolved={noop}
        onSettingsClick={noop}
        onTrendHistoryClick={noop}
        onTariffRadarClick={noop}
        onSmartPlugImportClick={noop}
      />,
    )

    expect(await screen.findByText('No Status yet')).toBeInTheDocument()
    expect(screen.getAllByRole('button', { name: /Log reading/ })).toHaveLength(1)
  })

  it('renders no Log Reading trigger while the skeleton is showing', () => {
    render(
      <DashboardPage
        household={household}
        supportsFederatedLogout={true}
        email={null}
        status={null}
        statusLoading={true}
        tariffCheck={null}
        playStatusEntranceAnimation={true}
        logSheetOpen={false}
        onLogSheetOpenChange={noop}
        logEventOpen={false}
        onLogEventOpenChange={noop}
        onReadingSaved={noop}
        openRegressionPrompt={null}
        onRegressionResolved={noop}
        onSettingsClick={noop}
        onTrendHistoryClick={noop}
        onTariffRadarClick={noop}
        onSmartPlugImportClick={noop}
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
        supportsFederatedLogout={true}
        email={null}
        status={null}
        statusLoading={false}
        tariffCheck={null}
        playStatusEntranceAnimation={true}
        logSheetOpen={false}
        onLogSheetOpenChange={noop}
        logEventOpen={false}
        onLogEventOpenChange={noop}
        onReadingSaved={noop}
        openRegressionPrompt={null}
        onRegressionResolved={noop}
        onSettingsClick={onSettingsClick}
        onTrendHistoryClick={noop}
        onTariffRadarClick={noop}
        onSmartPlugImportClick={noop}
      />,
    )

    expect(screen.getAllByText('Trend History').length).toBeGreaterThan(0)
    await user.click(screen.getAllByRole('button', { name: 'Settings' })[0])
    expect(onSettingsClick).toHaveBeenCalledOnce()
  })

  it('renders the top nav chrome variant through the real page prop chain, and its Settings tap also calls onSettingsClick (Story 8.1)', async () => {
    // Regression guard: most tests in this file click whichever nav variant getAllByRole(...)[0]
    // returns. This one scopes to the wide:flex top-nav variant by data-slot (DOM order flipped in
    // Story 10.3, so an index no longer identifies it) and drives it through the real page prop chain.
    const user = userEvent.setup()
    const onSettingsClick = vi.fn()
    render(
      <DashboardPage
        household={household}
        supportsFederatedLogout={true}
        email="member@example.com"
        status={null}
        statusLoading={false}
        tariffCheck={null}
        playStatusEntranceAnimation={true}
        logSheetOpen={false}
        onLogSheetOpenChange={noop}
        logEventOpen={false}
        onLogEventOpenChange={noop}
        onReadingSaved={noop}
        openRegressionPrompt={null}
        onRegressionResolved={noop}
        onSettingsClick={onSettingsClick}
        onTrendHistoryClick={noop}
        onTariffRadarClick={noop}
        onSmartPlugImportClick={noop}
      />,
    )

    expect(screen.getAllByRole('button', { name: 'Settings' })).toHaveLength(2)
    const topNav = document.querySelector<HTMLElement>('nav[data-slot="nav-chrome-top"]')!
    await user.click(within(topNav).getByRole('button', { name: 'Settings' }))
    expect(onSettingsClick).toHaveBeenCalledOnce()

    await user.click(screen.getByRole('button', { name: 'Account menu' }))
    expect(await screen.findByText('member@example.com')).toBeInTheDocument()
  })

  it('mounts the top nav first and the bottom nav last in <main>, so focus order follows reading order (Story 10.3)', () => {
    render(
      <DashboardPage
        household={household}
        supportsFederatedLogout={true}
        email={null}
        status={null}
        statusLoading={false}
        tariffCheck={null}
        playStatusEntranceAnimation={true}
        logSheetOpen={false}
        onLogSheetOpenChange={noop}
        logEventOpen={false}
        onLogEventOpenChange={noop}
        onReadingSaved={noop}
        openRegressionPrompt={null}
        onRegressionResolved={noop}
        onSettingsClick={noop}
        onTrendHistoryClick={noop}
        onTariffRadarClick={noop}
        onSmartPlugImportClick={noop}
      />,
    )

    const main = screen.getByRole('main')
    expect(main.firstElementChild).toHaveAttribute('data-slot', 'nav-chrome-top')
    const navs = main.querySelectorAll(':scope > nav[data-slot]')
    expect(navs[navs.length - 1]).toHaveAttribute('data-slot', 'nav-chrome-bottom')
    expect(main.querySelectorAll('nav[data-slot]')).toHaveLength(2)
  })

  it('tapping Trend History in the nav chrome calls onTrendHistoryClick (Story 4.1 — the standalone History trigger it replaces was removed)', async () => {
    const user = userEvent.setup()
    const onTrendHistoryClick = vi.fn()
    render(
      <DashboardPage
        household={household}
        supportsFederatedLogout={true}
        email={null}
        status={null}
        statusLoading={false}
        tariffCheck={null}
        playStatusEntranceAnimation={true}
        logSheetOpen={false}
        onLogSheetOpenChange={noop}
        logEventOpen={false}
        onLogEventOpenChange={noop}
        onReadingSaved={noop}
        openRegressionPrompt={null}
        onRegressionResolved={noop}
        onSettingsClick={noop}
        onTrendHistoryClick={onTrendHistoryClick}
        onTariffRadarClick={noop}
        onSmartPlugImportClick={noop}
      />,
    )

    expect(screen.queryByRole('button', { name: 'View reading history' })).not.toBeInTheDocument()
    await user.click(screen.getAllByRole('button', { name: 'Trend History' })[0])
    expect(onTrendHistoryClick).toHaveBeenCalledOnce()
  })

  it('renders the Smart Plug Import entry point in the topbar and calls onSmartPlugImportClick when tapped (Story 3.5)', async () => {
    const user = userEvent.setup()
    const onSmartPlugImportClick = vi.fn()
    render(
      <DashboardPage
        household={household}
        supportsFederatedLogout={true}
        email={null}
        status={null}
        statusLoading={false}
        tariffCheck={null}
        playStatusEntranceAnimation={true}
        logSheetOpen={false}
        onLogSheetOpenChange={noop}
        logEventOpen={false}
        onLogEventOpenChange={noop}
        onReadingSaved={noop}
        openRegressionPrompt={null}
        onRegressionResolved={noop}
        onSettingsClick={noop}
        onTrendHistoryClick={noop}
        onTariffRadarClick={noop}
        onSmartPlugImportClick={onSmartPlugImportClick}
      />,
    )

    const trigger = screen.getByRole('button', { name: 'Import Smart Plug data' })
    expect(trigger).toBeInTheDocument()
    await user.click(trigger)
    expect(onSmartPlugImportClick).toHaveBeenCalledOnce()
  })

  it('renders visible short-word labels on both header icon buttons at the wide breakpoint, alongside their unchanged aria-label/title (AC #3, #4)', () => {
    render(
      <DashboardPage
        household={household}
        supportsFederatedLogout={true}
        email={null}
        status={null}
        statusLoading={false}
        tariffCheck={null}
        playStatusEntranceAnimation={true}
        logSheetOpen={false}
        onLogSheetOpenChange={noop}
        logEventOpen={false}
        onLogEventOpenChange={noop}
        onReadingSaved={noop}
        openRegressionPrompt={null}
        onRegressionResolved={noop}
        onSettingsClick={noop}
        onTrendHistoryClick={noop}
        onTariffRadarClick={noop}
        onSmartPlugImportClick={noop}
      />,
    )

    const eventButton = screen.getByRole('button', { name: 'Log an Event' })
    const importButton = screen.getByRole('button', { name: 'Import Smart Plug data' })

    const eventLabel = within(eventButton).getByText('Event')
    expect(eventLabel).toHaveClass('hidden', 'wide:inline')
    const importLabel = within(importButton).getByText('Import')
    expect(importLabel).toHaveClass('hidden', 'wide:inline')
  })

  it('constrains the page content to a centered 900px column at the wide breakpoint (AC #1)', () => {
    const status: StatusDto = { status: 'withinRange', paceToDateKwh: 1000, baselineToDateKwh: 1000, isLowConfidence: false }
    render(
      <DashboardPage
        household={household}
        supportsFederatedLogout={true}
        email={null}
        status={status}
        statusLoading={false}
        tariffCheck={null}
        playStatusEntranceAnimation={true}
        logSheetOpen={false}
        onLogSheetOpenChange={noop}
        logEventOpen={false}
        onLogEventOpenChange={noop}
        onReadingSaved={noop}
        openRegressionPrompt={null}
        onRegressionResolved={noop}
        onSettingsClick={noop}
        onTrendHistoryClick={noop}
        onTariffRadarClick={noop}
        onSmartPlugImportClick={noop}
      />,
    )

    const heading = screen.getByRole('heading', { name: 'Energy Tracker' })
    const logReadingButton = screen.getByRole('button', { name: /Log reading/ })
    // data-slot is a stable, visibility-independent hook (same convention as nav-chrome.tsx's
    // data-slot="nav-chrome-bottom"/"nav-chrome-top") for both this test and the e2e viewport spec.
    const wrapper = document.querySelector('[data-slot="dashboard-content"]')
    expect(wrapper).not.toBeNull()
    expect(wrapper).toHaveClass('wide:mx-auto', 'wide:w-full', 'wide:max-w-[900px]')
    expect(wrapper).toContainElement(heading)
    expect(wrapper).toContainElement(logReadingButton)
  })

  it('does not render the invite-generation panel — relocated to Settings so it never competes with the Status card for visual weight (AC #10)', () => {
    const status: StatusDto = { status: 'withinRange', paceToDateKwh: 1000, baselineToDateKwh: 1000, isLowConfidence: false }
    render(
      <DashboardPage
        household={household}
        supportsFederatedLogout={true}
        email={null}
        status={status}
        statusLoading={false}
        tariffCheck={null}
        playStatusEntranceAnimation={true}
        logSheetOpen={false}
        onLogSheetOpenChange={noop}
        logEventOpen={false}
        onLogEventOpenChange={noop}
        onReadingSaved={noop}
        openRegressionPrompt={null}
        onRegressionResolved={noop}
        onSettingsClick={noop}
        onTrendHistoryClick={noop}
        onTariffRadarClick={noop}
        onSmartPlugImportClick={noop}
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
              trendingThresholdToDateKwh: 50,
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
        supportsFederatedLogout={true}
        email={null}
        status={status}
        statusLoading={false}
        tariffCheck={null}
        playStatusEntranceAnimation={true}
        logSheetOpen={false}
        onLogSheetOpenChange={noop}
        logEventOpen={false}
        onLogEventOpenChange={noop}
        onReadingSaved={noop}
        openRegressionPrompt={null}
        onRegressionResolved={noop}
        onSettingsClick={noop}
        onTrendHistoryClick={noop}
        onTariffRadarClick={noop}
        onSmartPlugImportClick={noop}
      />,
    )

    await user.click(screen.getByRole('button', { name: 'How was this calculated?' }))
    expect(await screen.findByText('Status calculation')).toBeInTheDocument()

    rerender(
      <DashboardPage
        household={household}
        supportsFederatedLogout={true}
        email={null}
        status={status}
        statusLoading={false}
        tariffCheck={null}
        playStatusEntranceAnimation={true}
        logSheetOpen={false}
        onLogSheetOpenChange={noop}
        logEventOpen={false}
        onLogEventOpenChange={noop}
        onReadingSaved={noop}
        openRegressionPrompt={regressionPrompt()}
        onRegressionResolved={noop}
        onSettingsClick={noop}
        onTrendHistoryClick={noop}
        onTariffRadarClick={noop}
        onSmartPlugImportClick={noop}
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
              trendingThresholdToDateKwh: 50,
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
        supportsFederatedLogout={true}
        email={null}
        status={status}
        statusLoading={false}
        tariffCheck={null}
        playStatusEntranceAnimation={true}
        logSheetOpen={false}
        onLogSheetOpenChange={noop}
        logEventOpen={false}
        onLogEventOpenChange={noop}
        onReadingSaved={noop}
        openRegressionPrompt={null}
        onRegressionResolved={noop}
        onSettingsClick={noop}
        onTrendHistoryClick={noop}
        onTariffRadarClick={noop}
        onSmartPlugImportClick={noop}
      />,
    )

    await user.click(screen.getByRole('button', { name: 'How was this calculated?' }))
    expect(await screen.findByText('Status calculation')).toBeInTheDocument()

    // A transient status refresh failure (background sync, etc.) drops status to null.
    rerender(
      <DashboardPage
        household={household}
        supportsFederatedLogout={true}
        email={null}
        status={null}
        statusLoading={false}
        tariffCheck={null}
        playStatusEntranceAnimation={true}
        logSheetOpen={false}
        onLogSheetOpenChange={noop}
        logEventOpen={false}
        onLogEventOpenChange={noop}
        onReadingSaved={noop}
        openRegressionPrompt={null}
        onRegressionResolved={noop}
        onSettingsClick={noop}
        onTrendHistoryClick={noop}
        onTariffRadarClick={noop}
        onSmartPlugImportClick={noop}
      />,
    )

    await waitFor(() => expect(screen.queryByText('Status calculation')).not.toBeInTheDocument())

    // Status repopulates — the dialog must not silently reopen on its own.
    rerender(
      <DashboardPage
        household={household}
        supportsFederatedLogout={true}
        email={null}
        status={status}
        statusLoading={false}
        tariffCheck={null}
        playStatusEntranceAnimation={true}
        logSheetOpen={false}
        onLogSheetOpenChange={noop}
        logEventOpen={false}
        onLogEventOpenChange={noop}
        onReadingSaved={noop}
        openRegressionPrompt={null}
        onRegressionResolved={noop}
        onSettingsClick={noop}
        onTrendHistoryClick={noop}
        onTariffRadarClick={noop}
        onSmartPlugImportClick={noop}
      />,
    )

    expect(screen.queryByText('Status calculation')).not.toBeInTheDocument()
  })

  it('renders the Tariff Check card after the Status card when a reminder is due, and not at all when tariffCheck is null', async () => {
    const status: StatusDto = { status: 'withinRange', paceToDateKwh: 1060, baselineToDateKwh: 1300, isLowConfidence: false }
    const { rerender } = render(
      <DashboardPage
        household={household}
        supportsFederatedLogout={true}
        email={null}
        status={status}
        statusLoading={false}
        tariffCheck={{ isDue: true, gateOpensAtUtc: '2026-06-01T00:00:00+00:00' }}
        playStatusEntranceAnimation={true}
        logSheetOpen={false}
        onLogSheetOpenChange={noop}
        logEventOpen={false}
        onLogEventOpenChange={noop}
        onReadingSaved={noop}
        openRegressionPrompt={null}
        onRegressionResolved={noop}
        onSettingsClick={noop}
        onTrendHistoryClick={noop}
        onTariffRadarClick={noop}
        onSmartPlugImportClick={noop}
      />,
    )

    const statusHeadline = await screen.findByText('Quiet week.')
    const tariffCheckButton = screen.getByText(/worth a look/i)
    expect(
      statusHeadline.compareDocumentPosition(tariffCheckButton) & Node.DOCUMENT_POSITION_FOLLOWING,
    ).toBeTruthy()

    rerender(
      <DashboardPage
        household={household}
        supportsFederatedLogout={true}
        email={null}
        status={status}
        statusLoading={false}
        tariffCheck={null}
        playStatusEntranceAnimation={true}
        logSheetOpen={false}
        onLogSheetOpenChange={noop}
        logEventOpen={false}
        onLogEventOpenChange={noop}
        onReadingSaved={noop}
        openRegressionPrompt={null}
        onRegressionResolved={noop}
        onSettingsClick={noop}
        onTrendHistoryClick={noop}
        onTariffRadarClick={noop}
        onSmartPlugImportClick={noop}
      />,
    )

    expect(screen.queryByText(/worth a look/i)).not.toBeInTheDocument()
  })

  // Story 11.16 (AC #6, #10): readings the server rejected surface under the Status card.
  describe('rejected readings card (Story 11.16)', () => {
    const status: StatusDto = { status: 'withinRange', paceToDateKwh: 1060, baselineToDateKwh: 1300, isLowConfidence: false }

    function renderDashboard(onReadingSaved: () => void = noop) {
      return render(
        <DashboardPage
          household={household}
          supportsFederatedLogout={true}
          email={null}
          status={status}
          statusLoading={false}
          tariffCheck={{ isDue: true, gateOpensAtUtc: '2026-06-01T00:00:00+00:00' }}
          playStatusEntranceAnimation={true}
          logSheetOpen={false}
          onLogSheetOpenChange={noop}
          logEventOpen={false}
          onLogEventOpenChange={noop}
          onReadingSaved={onReadingSaved}
          openRegressionPrompt={null}
          onRegressionResolved={noop}
          onSettingsClick={noop}
          onTrendHistoryClick={noop}
          onTariffRadarClick={noop}
          onSmartPlugImportClick={noop}
        />,
      )
    }

    async function seedRejected() {
      await enqueue({ householdId: household.id, kwhValue: 4830.25, readingTimestamp: '2026-11-02T10:00:00Z', idempotencyKey: 'rejected-1' })
      await markRejected('rejected-1', { status: 400, detail: 'Reading timestamp is too far in the future.' })
    }

    it('is absent when the queue holds no rejected entry', async () => {
      await enqueue({ householdId: household.id, kwhValue: 1, readingTimestamp: '2026-11-02T10:00:00Z', idempotencyKey: 'pending-1' })
      renderDashboard()

      await screen.findByText('Quiet week.')
      // two reads after the card's own mount read, so its answer has landed before absence is asserted
      await act(async () => {
        await listNeedsAttention(household.id)
        await listNeedsAttention(household.id)
      })

      expect(screen.queryByText(/needs? your attention/)).not.toBeInTheDocument()
    })

    it('renders after the Status card and before the Tariff Check card, so reading order is Tab order', async () => {
      await seedRejected()
      renderDashboard()

      const statusHeadline = await screen.findByText('Quiet week.')
      const card = await screen.findByRole('heading', { name: '1 reading needs your attention' })
      const tariffCheck = screen.getByText(/worth a look/i)

      expect(statusHeadline.compareDocumentPosition(card) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy()
      expect(card.compareDocumentPosition(tariffCheck) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy()
    })

    it('a successful resend calls onReadingSaved, the callback a foreground save uses', async () => {
      const user = userEvent.setup()
      await seedRejected()
      vi.stubGlobal('fetch', vi.fn(() => Promise.resolve(new Response(JSON.stringify({ id: 'r1', kwhValue: 4830.25, readingTimestamp: '2026-11-02T10:00:00Z' }), { status: 200 }))))
      const onReadingSaved = vi.fn()
      renderDashboard(onReadingSaved)

      await user.click(await screen.findByRole('button', { name: /^Correct and resend the reading/ }))
      await user.click(within(await screen.findByRole('dialog')).getByRole('button', { name: 'Resend' }))

      await waitFor(() => expect(onReadingSaved).toHaveBeenCalledOnce())
    })
  })
})
