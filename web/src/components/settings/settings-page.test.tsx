import 'fake-indexeddb/auto'
import { act, render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { IDBFactory } from 'fake-indexeddb'
import { SettingsPage } from './settings-page'
import { enqueue } from '@/lib/offline-queue'
import { HouseholdLocaleContext } from '@/lib/household-locale-context'

function jsonResponse(body: object | null, status = 200) {
  return new Response(body === null ? null : JSON.stringify(body), { status })
}

const householdId = '11111111-1111-1111-1111-111111111111'

function stubFetch(
  meterReadingsResponse: () => Response = () => jsonResponse(null),
  logoutResponse: () => Promise<Response> = () => Promise.resolve(jsonResponse({ redirectUrl: '/' })),
) {
  const fetchMock = vi.fn((input: string | URL | Request, _init?: RequestInit) => {
      const url = String(input)
      if (url === '/logout') {
        return logoutResponse()
      }
      if (url === '/api/rooms' || url === '/api/power-points' || url === '/api/devices') {
        return Promise.resolve(jsonResponse([]))
      }
      if (url === `/api/households/${householdId}`) {
        return Promise.resolve(jsonResponse({ id: householdId, locale: 'en-US', currency: 'USD', yearlyBaselineKwh: null, version: 0 }))
      }
      if (url === `/api/households/${householdId}/ai-plausibility`) {
        return Promise.resolve(jsonResponse({ enabled: false, backendConfigured: false, backendLabel: null, version: 0 }))
      }
      if (url === '/api/meter-readings') {
        return Promise.resolve(meterReadingsResponse())
      }
      // Story 11.9: the Household section loads both lists on mount.
      if (url === '/api/household-members') {
        return Promise.resolve(
          jsonResponse({ members: [{ id: 'm-me', displayName: 'Ralf', isCurrentMember: true, joinedAtUtc: '2026-08-12T12:00:00Z' }] }),
        )
      }
      if (url === '/api/household-invites') {
        return Promise.resolve(jsonResponse({ invites: [] }))
      }
      return Promise.resolve(jsonResponse(null))
  })
  vi.stubGlobal('fetch', fetchMock)
  return fetchMock
}

function renderSettingsPage(supportsFederatedLogout: boolean) {
  render(
    <SettingsPage
      householdId={householdId}
      supportsFederatedLogout={supportsFederatedLogout}
      email={null}
      onBack={() => {}}
      onTrendHistoryClick={() => {}}
      onTariffRadarClick={() => {}}
    />,
  )
}

describe('SettingsPage', () => {
  beforeEach(() => {
    // jsdom has no native IndexedDB implementation — fake-indexeddb/auto stubs `indexedDB`
    // globally. Reset it between tests so each test starts from an empty offline queue.
    globalThis.indexedDB = new IDBFactory()
  })

  afterEach(() => {
    vi.unstubAllGlobals()
  })

  // Story 11.14 (audit C10): a restore gives every room, power point and device a new id. The scaffold
  // manager loads them once on mount and then renames, moves and archives by id, so without a reload
  // every one of those requests would target an id that no longer exists.
  it('loads the rooms, power points and devices again once a restore completes', async () => {
    vi.useFakeTimers({ shouldAdvanceTime: true })
    try {
      const fetchMock = stubFetch()
      const defaultImplementation = fetchMock.getMockImplementation()!
      let restoreCompleted = false
      fetchMock.mockImplementation((input: string | URL | Request, init?: RequestInit) => {
        const url = String(input)
        if (url === '/api/rooms') {
          return Promise.resolve(jsonResponse([{ id: restoreCompleted ? 'room-new' : 'room-old', name: 'Kitchen', archivedAt: null }]))
        }
        if (url === '/api/household-import') {
          return Promise.resolve(jsonResponse({
            token: 'aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee',
            summary: {
              householdMembers: 0, hasMainMeter: false, meterReadings: 0, meterRegressionPrompts: 0, tariffs: 0, events: 0, rooms: 1,
              powerPoints: 0, devices: 0, smartPlugReadings: 0, statusSnapshots: 0, auditCorrections: 0, memberDisplayNames: [],
            },
          }))
        }
        if (url.endsWith('/confirm')) {
          return Promise.resolve(jsonResponse({ jobId: 'job-1' }, 202))
        }
        if (url === '/api/jobs/job-1') {
          restoreCompleted = true
          return Promise.resolve(jsonResponse({ id: 'job-1', status: 'completed', importStatus: null, errorMessage: null }))
        }
        return defaultImplementation(input, init)
      })
      const roomLoads = () => fetchMock.mock.calls.filter(([input]) => String(input) === '/api/rooms').length
      renderSettingsPage(true)
      await screen.findByText('Kitchen')
      expect(roomLoads()).toBe(1)

      const fileInput = document.querySelector('input[type="file"][accept="application/json"]') as HTMLInputElement
      await userEvent.upload(fileInput, new File(['{"formatVersion":"v2"}'], 'export.json', { type: 'application/json' }))
      await screen.findByText(/replace all data with this file/i)
      const user = userEvent.setup({ advanceTimers: vi.advanceTimersByTime })
      await user.click(screen.getByRole('button', { name: 'Replace all data' }))
      await vi.advanceTimersByTimeAsync(2000)
      await screen.findByText(/restore complete/i)

      await waitFor(() => expect(roomLoads()).toBe(2))
    } finally {
      vi.useRealTimers()
    }
  })

  it('tells its owner when a restore completes so it can reload what it holds', async () => {
    vi.useFakeTimers({ shouldAdvanceTime: true })
    try {
      const fetchMock = stubFetch()
      const defaultImplementation = fetchMock.getMockImplementation()!
      fetchMock.mockImplementation((input: string | URL | Request, init?: RequestInit) => {
        const url = String(input)
        if (url === '/api/household-import') {
          return Promise.resolve(jsonResponse({
            token: 'aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee',
            summary: {
              householdMembers: 0, hasMainMeter: false, meterReadings: 0, meterRegressionPrompts: 0, tariffs: 0, events: 0, rooms: 0,
              powerPoints: 0, devices: 0, smartPlugReadings: 0, statusSnapshots: 0, auditCorrections: 0, memberDisplayNames: [],
            },
          }))
        }
        if (url.endsWith('/confirm')) {
          return Promise.resolve(jsonResponse({ jobId: 'job-1' }, 202))
        }
        if (url === '/api/jobs/job-1') {
          return Promise.resolve(jsonResponse({ id: 'job-1', status: 'completed', importStatus: null, errorMessage: null }))
        }
        return defaultImplementation(input, init)
      })
      const onDataRestored = vi.fn()
      render(
        <SettingsPage
          householdId={householdId}
          supportsFederatedLogout={true}
          email={null}
          onBack={() => {}}
          onTrendHistoryClick={() => {}}
          onTariffRadarClick={() => {}}
          onDataRestored={onDataRestored}
        />,
      )
      const fileInput = document.querySelector('input[type="file"][accept="application/json"]') as HTMLInputElement
      await userEvent.upload(fileInput, new File(['{"formatVersion":"v2"}'], 'export.json', { type: 'application/json' }))
      await screen.findByText(/replace all data with this file/i)
      const user = userEvent.setup({ advanceTimers: vi.advanceTimersByTime })
      await user.click(screen.getByRole('button', { name: 'Replace all data' }))
      await vi.advanceTimersByTimeAsync(2000)
      await screen.findByText(/restore complete/i)

      expect(onDataRestored).toHaveBeenCalledTimes(1)
    } finally {
      vi.useRealTimers()
    }
  })

  it('mounts the top nav first and the bottom nav last in <main> (Story 10.3)', async () => {
    stubFetch()
    renderSettingsPage(true)

    const main = screen.getByRole('main')
    expect(main.firstElementChild).toHaveAttribute('data-slot', 'nav-chrome-top')
    const navs = main.querySelectorAll(':scope > nav[data-slot]')
    expect(navs[navs.length - 1]).toHaveAttribute('data-slot', 'nav-chrome-bottom')
    expect(main.lastElementChild).toBe(navs[navs.length - 1])
    await screen.findByRole('button', { name: 'Export data' })
  })

  it('no longer renders a Smart Plug Import panel — moved to its own Dashboard-launched screen (Story 3.5, AC #1)', async () => {
    stubFetch()

    renderSettingsPage(true)

    expect(await screen.findByRole('heading', { name: 'Settings' })).toBeInTheDocument()
    expect(screen.queryByText('Smart Plug Import')).not.toBeInTheDocument()
    expect(screen.queryByText('Drop a file here, or choose one to upload.')).not.toBeInTheDocument()
  })

  describe('660px column and labeled sections (Story 8.5 Tasks 1-2, AC #1)', () => {
    it('wraps the header and card stack in a data-slot="settings-content" wrapper', async () => {
      stubFetch()

      renderSettingsPage(true)

      expect(await screen.findByRole('heading', { name: 'Settings' })).toBeInTheDocument()
      expect(document.querySelector('[data-slot="settings-content"]')).toBeInTheDocument()
    })

    it('renders all 5 section-label headings', async () => {
      stubFetch()

      renderSettingsPage(true)

      // "Yearly Baseline" appears both as YearlyBaselineForm's own now-wide:hidden <h2> and the
      // new SectionLabel's hidden wide:block <h2> — jsdom applies neither `hidden` class, so both
      // are simultaneously queryable, exactly like this file's existing findAllByRole(...)[0]
      // dual-nav-match workaround above. Same for "AI Plausibility Check" (AiPlausibilityForm's
      // own heading was aligned to this exact wording during code review) and "Rooms, Power
      // Points & Devices" (TaggingScaffoldManager's own heading, reused verbatim by its SectionLabel).
      expect((await screen.findAllByRole('heading', { name: 'Yearly Baseline', level: 2 }))[0]).toBeInTheDocument()
      expect((await screen.findAllByRole('heading', { name: 'AI Plausibility Check', level: 2 }))[0]).toBeInTheDocument()
      expect(await screen.findByRole('heading', { name: 'Household', level: 2 })).toBeInTheDocument()
      expect((await screen.findAllByRole('heading', { name: 'Rooms, Power Points & Devices', level: 2 }))[0]).toBeInTheDocument()
      expect(await screen.findByRole('heading', { name: 'Data', level: 2 })).toBeInTheDocument()
    })

    it('renders "Invite a member" as a row inside the Household section, not a standalone unheaded button', async () => {
      stubFetch()

      renderSettingsPage(true)

      // Story 8.5's InviteMemberRow dual-renders — two matches (narrow-mode standalone panel's
      // own button, wide-mode row's own label), same accessible name.
      expect(await screen.findByRole('heading', { name: 'Settings' })).toBeInTheDocument()
      expect(screen.getAllByRole('button', { name: 'Invite a member' })).toHaveLength(2)
    })
  })

  describe('section layout (Story 8.8 Task 1; amended by Story 11.9, approved mock key-household-members-2026-10-17)', () => {
    // Each SectionLabel is the first child of its section wrapper (`hidden wide:block` <h2>).
    function sectionOf(name: string) {
      const label = screen
        .getAllByRole('heading', { name, level: 2 })
        .find((h) => h.className.includes('wide:block'))
      if (!label?.parentElement) throw new Error(`No SectionLabel for ${name}`)
      return label.parentElement
    }

    // The Household card carries its own heading (an <h2> that is visible at every width), so it has no SectionLabel.
    function householdSection() {
      // GlassCard wraps its card in a "glass-card-stack"; the section block is that stack's parent.
      const block = document.querySelector('[data-slot="household-members"]')?.closest('[data-slot="glass-card-stack"]')?.parentElement
      if (!block) throw new Error('No Household section')
      return block
    }

    it('no longer pairs AI Plausibility and Household: each is its own full-width block, and no wrapper is a wide:flex-row', async () => {
      stubFetch()
      renderSettingsPage(true)
      expect(await screen.findByRole('heading', { name: 'Household', level: 2 })).toBeInTheDocument()

      const ai = sectionOf('AI Plausibility Check')
      const household = householdSection()

      expect(ai.parentElement).toBe(household.parentElement)
      expect(ai).not.toBe(household)
      expect(ai.contains(household)).toBe(false)
      expect(household.contains(ai)).toBe(false)
      for (const block of [ai, household]) {
        expect(block.className).not.toContain('wide:flex-1')
        expect(block.parentElement?.className).not.toContain('wide:flex-row')
      }
    })

    it('keeps DOM order Baseline → AI → Household → Rooms → Data', async () => {
      stubFetch()
      renderSettingsPage(true)
      expect(await screen.findByRole('heading', { name: 'Household', level: 2 })).toBeInTheDocument()

      const sections = [
        sectionOf('Yearly Baseline'),
        sectionOf('AI Plausibility Check'),
        householdSection(),
        sectionOf('Rooms, Power Points & Devices'),
        sectionOf('Data'),
      ]
      for (let i = 0; i < sections.length - 1; i++) {
        expect(sections[i].compareDocumentPosition(sections[i + 1]) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy()
      }
    })

    it('renders the Household section with its members and open invites (Story 11.9, AC #1)', async () => {
      stubFetch()
      renderSettingsPage(true)

      expect(await screen.findByText('Ralf')).toBeInTheDocument()
      expect(screen.getByRole('button', { name: 'Leave household' })).toBeInTheDocument()
    })
  })

  it('threads onTrendHistoryClick through to the NavChrome Trend History tab (Story 4.1)', async () => {
    const user = userEvent.setup()
    const onTrendHistoryClick = vi.fn()
    vi.stubGlobal(
      'fetch',
      vi.fn((input: string | URL | Request) => {
        const url = String(input)
        if (url === '/api/rooms' || url === '/api/power-points' || url === '/api/devices') {
          return Promise.resolve(jsonResponse([]))
        }
        if (url === `/api/households/${householdId}`) {
          return Promise.resolve(jsonResponse({ id: householdId, locale: 'en-US', currency: 'USD', yearlyBaselineKwh: null, version: 0 }))
        }
        if (url === `/api/households/${householdId}/ai-plausibility`) {
          return Promise.resolve(jsonResponse({ enabled: false, backendConfigured: false, backendLabel: null, version: 0 }))
        }
        return Promise.resolve(jsonResponse(null))
      }),
    )

    render(
      <SettingsPage
        householdId={householdId}
        supportsFederatedLogout={true}
        email={null}
        onBack={() => {}}
        onTrendHistoryClick={onTrendHistoryClick}
        onTariffRadarClick={() => {}}
      />,
    )

    await user.click((await screen.findAllByRole('button', { name: 'Trend History' }))[0])
    expect(onTrendHistoryClick).toHaveBeenCalledOnce()
  })

  describe('Logoff control (Story 1.12, FR-33)', () => {
    function stubLocation() {
      const originalLocation = window.location
      const mockLocation = { ...originalLocation, href: '' }
      Object.defineProperty(window, 'location', { value: mockLocation, writable: true })
      return () => Object.defineProperty(window, 'location', { value: originalLocation, writable: true })
    }

    it('is visible and reachable directly from Settings (AC #1)', async () => {
      stubFetch()

      renderSettingsPage(true)

      expect(await screen.findByRole('button', { name: 'Log off' })).toBeInTheDocument()
    })

    it('opens a confirmation dialog before doing anything else', async () => {
      const user = userEvent.setup()
      stubFetch()
      renderSettingsPage(true)

      await user.click(await screen.findByRole('button', { name: 'Log off' }))

      expect(await screen.findByRole('heading', { name: 'Log off?' })).toBeInTheDocument()
    })

    it('POSTs /logout with the CSRF header and navigates to the returned URL when federated logout is supported and no readings are queued (AC #3, #5)', async () => {
      const user = userEvent.setup()
      const fetchMock = stubFetch(undefined, () => Promise.resolve(jsonResponse({ redirectUrl: 'https://tenant.example/oidc/logout?state=abc' })))
      const restoreLocation = stubLocation()
      renderSettingsPage(true)

      await user.click(await screen.findByRole('button', { name: 'Log off' }))
      await user.click(await screen.findByRole('button', { name: 'Yes, log off' }))

      await waitFor(() => expect(window.location.href).toBe('https://tenant.example/oidc/logout?state=abc'))
      const [, init] = fetchMock.mock.calls.find(([url]) => url === '/logout')!
      expect(init?.method).toBe('POST')
      expect(new Headers(init?.headers).get('X-Requested-With')).toBe('fetch')
      restoreLocation()
    })

    it('shows a warning instead of POSTing when federated logout is not supported, and proceeds only after Continue (AC #3)', async () => {
      const user = userEvent.setup()
      const fetchMock = stubFetch()
      const restoreLocation = stubLocation()
      renderSettingsPage(false)

      await user.click(await screen.findByRole('button', { name: 'Log off' }))
      await user.click(await screen.findByRole('button', { name: 'Yes, log off' }))

      expect(await screen.findByRole('heading', { name: 'You may still be signed in elsewhere' })).toBeInTheDocument()
      expect(window.location.href).toBe('')
      expect(fetchMock.mock.calls.some(([url]) => url === '/logout')).toBe(false)

      await user.click(screen.getByRole('button', { name: 'Continue' }))
      await waitFor(() => expect(window.location.href).toBe('/'))
      restoreLocation()
    })

    it('surfaces still-queued readings instead of silently discarding them, and only POSTs after an explicit choice (AC #3)', async () => {
      const user = userEvent.setup()
      // The flush attempt fails (simulating offline/transient failure) — the queued reading
      // survives, which is exactly what AC #4 requires to be surfaced explicitly.
      stubFetch(() => jsonResponse({ detail: 'unreachable' }, 503))
      await enqueue({ householdId, kwhValue: 4821.5, readingTimestamp: '2026-08-15T14:32:00Z', idempotencyKey: 'key-1' })
      const restoreLocation = stubLocation()
      renderSettingsPage(true)

      await user.click(await screen.findByRole('button', { name: 'Log off' }))
      await user.click(await screen.findByRole('button', { name: 'Yes, log off' }))

      expect(await screen.findByRole('heading', { name: 'Unsynced readings' })).toBeInTheDocument()
      expect(screen.getByText(/1 Meter Reading is still waiting to sync/)).toBeInTheDocument()
      expect(window.location.href).toBe('')

      await user.click(screen.getByRole('button', { name: 'Log off anyway' }))
      await waitFor(() => expect(window.location.href).toBe('/'))
      restoreLocation()
    })

    it('shows an inline error and keeps the dialog open when the POST fails (AC #10)', async () => {
      const user = userEvent.setup()
      stubFetch(undefined, () => Promise.reject(new TypeError('Failed to fetch')))
      const restoreLocation = stubLocation()
      renderSettingsPage(true)

      await user.click(await screen.findByRole('button', { name: 'Log off' }))
      await user.click(await screen.findByRole('button', { name: 'Yes, log off' }))

      expect(await screen.findByRole('alert')).toHaveTextContent("Logging off didn't work. Check your connection and try again.")
      expect(screen.getByRole('heading', { name: 'Log off?' })).toBeInTheDocument()
      expect(window.location.href).toBe('')
      expect(screen.getByRole('button', { name: 'Yes, log off' })).toBeEnabled()
      expect(screen.getByRole('button', { name: 'Cancel' })).toBeEnabled()
      restoreLocation()
    })

    it('disables the dialog buttons while the POST is in flight (AC #10)', async () => {
      const user = userEvent.setup()
      let finish: (response: Response) => void = () => {}
      const fetchMock = stubFetch(undefined, () => new Promise<Response>((resolve) => { finish = resolve }))
      const restoreLocation = stubLocation()
      renderSettingsPage(true)

      await user.click(await screen.findByRole('button', { name: 'Log off' }))
      await user.click(await screen.findByRole('button', { name: 'Yes, log off' }))

      // The queue check runs first; only once /logout has actually been requested is the POST in flight.
      await waitFor(() => expect(fetchMock.mock.calls.some(([url]) => url === '/logout')).toBe(true))
      // The confirm button reads "Checking…" for as long as the request is in flight.
      expect(screen.getByRole('button', { name: 'Checking…' })).toBeDisabled()
      expect(screen.getByRole('button', { name: 'Cancel' })).toBeDisabled()

      finish(jsonResponse({ redirectUrl: '/' }))
      await waitFor(() => expect(window.location.href).toBe('/'))
      restoreLocation()
    })

    it('does not navigate when the confirmation dialog is cancelled', async () => {
      const user = userEvent.setup()
      stubFetch()
      const restoreLocation = stubLocation()
      renderSettingsPage(true)

      await user.click(await screen.findByRole('button', { name: 'Log off' }))
      await user.click(await screen.findByRole('button', { name: 'Cancel' }))

      expect(screen.queryByRole('heading', { name: 'Log off?' })).not.toBeInTheDocument()
      expect(window.location.href).toBe('')
      restoreLocation()
    })
  })

  describe('Account group / Preferences card (Story 8.12, UX-DR33)', () => {
    function stubMatchMedia(initial: boolean) {
      let matches = initial
      const listeners = new Set<() => void>()
      const mql = {
        get matches() {
          return matches
        },
        media: '(min-width: 660px)',
        addEventListener: (_: string, cb: () => void) => listeners.add(cb),
        removeEventListener: (_: string, cb: () => void) => listeners.delete(cb),
      }
      vi.stubGlobal('matchMedia', vi.fn(() => mql))
      return {
        set(value: boolean) {
          matches = value
          act(() => listeners.forEach((cb) => cb()))
        },
      }
    }

    it('below 660px shows the Account heading, then the Preferences card, then Log off', async () => {
      stubFetch()
      renderSettingsPage(false)

      const heading = await screen.findByRole('heading', { level: 2, name: 'Account' })
      const card = screen.getByTestId('preferences-card')
      const logoff = screen.getByRole('button', { name: 'Log off' })
      expect(within(card).getByRole('radiogroup', { name: 'Appearance' })).toBeInTheDocument()
      expect(within(card).getByRole('radiogroup', { name: 'Language' })).toBeInTheDocument()
      expect(heading.compareDocumentPosition(card) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy()
      expect(card.compareDocumentPosition(logoff) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy()

      // The new Account wrapper must not break Log off: it still opens the confirm dialog.
      await userEvent.setup().click(logoff)
      expect(await screen.findByRole('heading', { name: 'Log off?' })).toBeInTheDocument()
    })

    it('at >=660px does not render the card or its radiogroups; Log off stays in the DOM (CSS-hidden)', async () => {
      stubMatchMedia(true)
      stubFetch()
      renderSettingsPage(false)

      await screen.findByRole('button', { name: 'Log off' })
      expect(screen.queryByTestId('preferences-card')).not.toBeInTheDocument()
      expect(screen.queryByRole('radiogroup', { name: 'Appearance' })).not.toBeInTheDocument()
      expect(screen.queryByRole('radiogroup', { name: 'Language' })).not.toBeInTheDocument()
      expect(screen.getByTestId('settings-account-group')).toHaveClass('wide:hidden')
    })

    it('unmounts the card when the viewport crosses 660px, even with a Language save in flight', async () => {
      const media = stubMatchMedia(false)
      const user = userEvent.setup()
      let resolvePut!: (r: Response) => void
      const setLocale = vi.fn()
      stubFetch()
      const baseFetch = globalThis.fetch as (input: string | URL | Request, init?: RequestInit) => Promise<Response>
      vi.stubGlobal(
        'fetch',
        vi.fn((input: string | URL | Request, init?: RequestInit) =>
          String(input) === `/api/households/${householdId}/locale`
            ? new Promise<Response>((r) => (resolvePut = r))
            : baseFetch(input, init),
        ),
      )
      render(
        <HouseholdLocaleContext.Provider value={{ locale: 'en-US', setLocale }}>
          <SettingsPage
            householdId={householdId}
            supportsFederatedLogout={false}
            email={null}
            onBack={() => {}}
            onTrendHistoryClick={() => {}}
            onTariffRadarClick={() => {}}
          />
        </HouseholdLocaleContext.Provider>,
      )

      await user.click(await screen.findByRole('radio', { name: 'Deutsch' }))
      expect(resolvePut).toBeDefined()
      media.set(true)
      expect(screen.queryByTestId('preferences-card')).not.toBeInTheDocument()

      // The in-flight save still lands in App-level context; only the card's announcement is dropped.
      await act(async () => resolvePut(new Response('{}', { status: 200 })))
      await waitFor(() => expect(setLocale).toHaveBeenCalledWith('de-DE'))
    })
  })
})
