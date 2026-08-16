import { render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { afterEach, describe, expect, it, vi } from 'vitest'
import App from './App'

// Routes only /api/session to the given response; every other call (e.g. Story 2.3's
// meter-regression-prompts/open mount check) gets a benign 200-empty-body — the same shape the
// real backend returns when nothing's open — rather than silently echoing back the session body.
function mockSession(response: object | null, status = 200) {
  vi.stubGlobal(
    'fetch',
    vi.fn((input: string | URL | Request) => {
      const url = String(input)
      if (url === '/api/session') {
        return Promise.resolve(new Response(response === null ? null : JSON.stringify(response), { status }))
      }
      return Promise.resolve(new Response(null, { status: 200 }))
    }),
  )
}

function jsonResponse(body: object | null, status = 200) {
  return new Response(body === null ? null : JSON.stringify(body), { status })
}

// Routes fetch calls by (method, URL) pair so a single test can mock both /api/session and a
// second endpoint (e.g. the invite preview/accept calls) with distinct responses.
function mockFetchRoutes(routes: Array<{ method: string; url: string; respond: () => Response }>) {
  vi.stubGlobal(
    'fetch',
    vi.fn((input: string | URL | Request, init?: RequestInit) => {
      const url = String(input)
      const method = (init?.method ?? 'GET').toUpperCase()
      const route = routes.find((r) => r.method === method && r.url === url)
      if (!route) {
        throw new Error(`Unmocked fetch: ${method} ${url}`)
      }

      return Promise.resolve(route.respond())
    }),
  )
}

describe('App', () => {
  afterEach(() => {
    vi.unstubAllGlobals()
    window.history.pushState({}, '', '/')
  })

  it('renders the placeholder shell once the session has a Household', async () => {
    mockSession({ hasHousehold: true, householdId: '11111111-1111-1111-1111-111111111111', locale: 'en-US', currency: 'USD' })

    render(<App />)

    expect(await screen.findByRole('heading', { name: 'Energy Tracker' })).toBeInTheDocument()
  })

  it('renders the Household-creation form when authenticated with no Household yet', async () => {
    mockSession({ hasHousehold: false, householdId: null, locale: null, currency: null })

    render(<App />)

    expect(await screen.findByRole('heading', { name: 'Set up your Household' })).toBeInTheDocument()
  })

  it('navigates to /login on an unauthenticated 401 response, never rendering the dashboard or the form', async () => {
    mockSession(null, 401)
    const originalLocation = window.location
    const mockLocation = { ...originalLocation, href: '' }
    Object.defineProperty(window, 'location', { value: mockLocation, writable: true })

    render(<App />)

    await vi.waitFor(() => expect(window.location.href).toBe('/login'))
    expect(screen.queryByRole('heading', { name: 'Energy Tracker' })).not.toBeInTheDocument()
    expect(screen.queryByRole('heading', { name: 'Set up your Household' })).not.toBeInTheDocument()

    Object.defineProperty(window, 'location', { value: originalLocation, writable: true })
  })

  describe('/join/{token} invite-accept flow', () => {
    it('navigates to /login with the invite path preserved as returnUrl on an unauthenticated 401', async () => {
      mockSession(null, 401)
      const originalLocation = window.location
      const mockLocation = { ...originalLocation, pathname: '/join/sometoken', href: '' }
      Object.defineProperty(window, 'location', { value: mockLocation, writable: true })

      render(<App />)

      await vi.waitFor(() => expect(window.location.href).toBe('/login?returnUrl=%2Fjoin%2Fsometoken'))

      Object.defineProperty(window, 'location', { value: originalLocation, writable: true })
    })

    it('renders the accept form when the invite token is valid', async () => {
      window.history.pushState({}, '', '/join/sometoken')
      mockFetchRoutes([
        { method: 'GET', url: '/api/session', respond: () => jsonResponse({ hasHousehold: false, householdId: null, locale: null, currency: null }) },
        { method: 'GET', url: '/api/household-invites/sometoken', respond: () => jsonResponse({ expiresAtUtc: '2026-08-21T00:00:00Z' }) },
      ])

      render(<App />)

      expect(await screen.findByRole('heading', { name: 'Join a Household' })).toBeInTheDocument()
    })

    it('renders invalid copy when the invite token is unknown (404)', async () => {
      window.history.pushState({}, '', '/join/sometoken')
      mockFetchRoutes([
        { method: 'GET', url: '/api/session', respond: () => jsonResponse({ hasHousehold: false, householdId: null, locale: null, currency: null }) },
        { method: 'GET', url: '/api/household-invites/sometoken', respond: () => jsonResponse(null, 404) },
      ])

      render(<App />)

      expect(await screen.findByText("This invite link is no longer valid. Ask the person who sent it for a new one.")).toBeInTheDocument()
    })

    it('renders invalid copy when the invite token is expired or already consumed (409)', async () => {
      window.history.pushState({}, '', '/join/sometoken')
      mockFetchRoutes([
        { method: 'GET', url: '/api/session', respond: () => jsonResponse({ hasHousehold: false, householdId: null, locale: null, currency: null }) },
        { method: 'GET', url: '/api/household-invites/sometoken', respond: () => jsonResponse(null, 409) },
      ])

      render(<App />)

      expect(await screen.findByText("This invite link is no longer valid. Ask the person who sent it for a new one.")).toBeInTheDocument()
    })

    it('accepting a valid invite transitions to the ready dashboard', async () => {
      const user = userEvent.setup()
      window.history.pushState({}, '', '/join/sometoken')
      mockFetchRoutes([
        { method: 'GET', url: '/api/session', respond: () => jsonResponse({ hasHousehold: false, householdId: null, locale: null, currency: null }) },
        { method: 'GET', url: '/api/household-invites/sometoken', respond: () => jsonResponse({ expiresAtUtc: '2026-08-21T00:00:00Z' }) },
        {
          method: 'POST',
          url: '/api/household-invites/sometoken/accept',
          respond: () => jsonResponse({ id: '11111111-1111-1111-1111-111111111111', locale: 'en-US', currency: 'USD' }),
        },
      ])

      render(<App />)

      const acceptButton = await screen.findByRole('button', { name: 'Join Household' })
      await user.click(acceptButton)

      expect(await screen.findByRole('heading', { name: 'Energy Tracker' })).toBeInTheDocument()
    })

    it('shows a brief message instead of the dashboard when a principal with a Household visits a stale invite link', async () => {
      window.history.pushState({}, '', '/join/sometoken')
      mockSession({ hasHousehold: true, householdId: '11111111-1111-1111-1111-111111111111', locale: 'en-US', currency: 'USD' })

      render(<App />)

      expect(
        await screen.findByText("You already belong to a Household, so this invite link doesn't apply to you."),
      ).toBeInTheDocument()
      expect(screen.queryByRole('heading', { name: 'Energy Tracker' })).not.toBeInTheDocument()
      expect(screen.getByRole('link', { name: 'Go to Energy Tracker' })).toHaveAttribute('href', '/')
    })

    it('still recognizes a trailing-slash invite path', async () => {
      window.history.pushState({}, '', '/join/sometoken/')
      mockFetchRoutes([
        { method: 'GET', url: '/api/session', respond: () => jsonResponse({ hasHousehold: false, householdId: null, locale: null, currency: null }) },
        { method: 'GET', url: '/api/household-invites/sometoken', respond: () => jsonResponse({ expiresAtUtc: '2026-08-21T00:00:00Z' }) },
      ])

      render(<App />)

      expect(await screen.findByRole('heading', { name: 'Join a Household' })).toBeInTheDocument()
    })

    it('renders a distinct error message, not invalid copy, when the preview check fails with a transient server error', async () => {
      window.history.pushState({}, '', '/join/sometoken')
      mockFetchRoutes([
        { method: 'GET', url: '/api/session', respond: () => jsonResponse({ hasHousehold: false, householdId: null, locale: null, currency: null }) },
        { method: 'GET', url: '/api/household-invites/sometoken', respond: () => jsonResponse(null, 500) },
      ])

      render(<App />)

      expect(await screen.findByText('Something went wrong loading this invite. Please try again.')).toBeInTheDocument()
      expect(screen.queryByText("This invite link is no longer valid. Ask the person who sent it for a new one.")).not.toBeInTheDocument()
      expect(screen.getByRole('link', { name: 'Go to Energy Tracker' })).toHaveAttribute('href', '/')
    })

    it('renders a distinct error message, not invalid copy, when accepting fails with a transient server error', async () => {
      const user = userEvent.setup()
      window.history.pushState({}, '', '/join/sometoken')
      mockFetchRoutes([
        { method: 'GET', url: '/api/session', respond: () => jsonResponse({ hasHousehold: false, householdId: null, locale: null, currency: null }) },
        { method: 'GET', url: '/api/household-invites/sometoken', respond: () => jsonResponse({ expiresAtUtc: '2026-08-21T00:00:00Z' }) },
        { method: 'POST', url: '/api/household-invites/sometoken/accept', respond: () => jsonResponse(null, 500) },
      ])

      render(<App />)

      const acceptButton = await screen.findByRole('button', { name: 'Join Household' })
      await user.click(acceptButton)

      expect(await screen.findByText('Something went wrong loading this invite. Please try again.')).toBeInTheDocument()
      expect(screen.queryByText("This invite link is no longer valid. Ask the person who sent it for a new one.")).not.toBeInTheDocument()
    })
  })

  describe('Settings navigation', () => {
    it('switches to the Settings surface and back via local view state, not a URL route', async () => {
      const user = userEvent.setup()
      mockFetchRoutes([
        { method: 'GET', url: '/api/session', respond: () => jsonResponse({ hasHousehold: true, householdId: '11111111-1111-1111-1111-111111111111', locale: 'en-US', currency: 'USD' }) },
        { method: 'GET', url: '/api/rooms', respond: () => jsonResponse([]) },
        { method: 'GET', url: '/api/power-points', respond: () => jsonResponse([]) },
        { method: 'GET', url: '/api/devices', respond: () => jsonResponse([]) },
        { method: 'GET', url: '/api/households/11111111-1111-1111-1111-111111111111', respond: () => jsonResponse({ id: '11111111-1111-1111-1111-111111111111', locale: 'en-US', currency: 'USD', yearlyBaselineKwh: null, version: 0 }) },
      ])

      render(<App />)

      await user.click(await screen.findByRole('button', { name: 'Settings' }))
      expect(await screen.findByRole('heading', { name: 'Settings' })).toBeInTheDocument()
      expect(window.location.pathname).toBe('/')

      await user.click(screen.getByRole('button', { name: 'Go to Energy Tracker' }))
      expect(await screen.findByRole('heading', { name: 'Energy Tracker' })).toBeInTheDocument()
    })
  })

  describe('invite-generation panel', () => {
    it('generates a shareable link and copies it to the clipboard', async () => {
      const user = userEvent.setup()
      const writeText = vi.fn().mockResolvedValue(undefined)
      Object.defineProperty(navigator, 'clipboard', { value: { writeText }, configurable: true, writable: true })

      mockFetchRoutes([
        { method: 'GET', url: '/api/session', respond: () => jsonResponse({ hasHousehold: true, householdId: '11111111-1111-1111-1111-111111111111', locale: 'en-US', currency: 'USD' }) },
        { method: 'POST', url: '/api/household-invites', respond: () => jsonResponse({ token: 'abcd1234', expiresAtUtc: '2026-08-21T00:00:00Z' }) },
      ])

      render(<App />)

      const generateButton = await screen.findByRole('button', { name: 'Invite a member' })
      await user.click(generateButton)

      const linkInput = await screen.findByLabelText('Invite link')
      expect(linkInput).toHaveValue(`${window.location.origin}/join/abcd1234`)

      const copyButton = screen.getByRole('button', { name: 'Copy link' })
      await user.click(copyButton)

      expect(writeText).toHaveBeenCalledWith(`${window.location.origin}/join/abcd1234`)
      expect(await screen.findByRole('button', { name: 'Copied' })).toBeInTheDocument()
    })

    it('shows an error instead of "Copied" when the clipboard write is rejected', async () => {
      const user = userEvent.setup()
      const writeText = vi.fn().mockRejectedValue(new Error('denied'))
      Object.defineProperty(navigator, 'clipboard', { value: { writeText }, configurable: true, writable: true })

      mockFetchRoutes([
        { method: 'GET', url: '/api/session', respond: () => jsonResponse({ hasHousehold: true, householdId: '11111111-1111-1111-1111-111111111111', locale: 'en-US', currency: 'USD' }) },
        { method: 'POST', url: '/api/household-invites', respond: () => jsonResponse({ token: 'abcd1234', expiresAtUtc: '2026-08-21T00:00:00Z' }) },
      ])

      render(<App />)

      const generateButton = await screen.findByRole('button', { name: 'Invite a member' })
      await user.click(generateButton)

      const copyButton = await screen.findByRole('button', { name: 'Copy link' })
      await user.click(copyButton)

      expect(await screen.findByText('Something went wrong creating the invite. Please try again.')).toBeInTheDocument()
      expect(screen.queryByRole('button', { name: 'Copied' })).not.toBeInTheDocument()
    })
  })

  describe('Meter Regression prompt (Story 2.3)', () => {
    const openPromptDto = {
      id: 'prompt-1',
      meterReadingId: 'reading-1',
      readingKwhValue: 412,
      readingTimestamp: '2026-08-16T19:42:00+00:00',
      previousMeterReadingId: 'reading-0',
      previousReadingKwhValue: 14302,
      previousReadingTimestamp: '2026-08-15T19:42:00+00:00',
      mainMeterDigitCapacityKwh: null,
    }

    it('a fetched open prompt on mount renders the regression dialog', async () => {
      mockFetchRoutes([
        { method: 'GET', url: '/api/session', respond: () => jsonResponse({ hasHousehold: true, householdId: '11111111-1111-1111-1111-111111111111', locale: 'en-US', currency: 'USD' }) },
        { method: 'GET', url: '/api/meter-regression-prompts/open', respond: () => jsonResponse(openPromptDto) },
      ])

      render(<App />)

      expect(await screen.findByRole('dialog', { name: 'That reading is lower than the last one' })).toBeInTheDocument()
    })

    it('the Log Reading trigger is inert (not in the accessibility tree) while a regression prompt is open — the dialog supersedes rather than stacks', async () => {
      mockFetchRoutes([
        { method: 'GET', url: '/api/session', respond: () => jsonResponse({ hasHousehold: true, householdId: '11111111-1111-1111-1111-111111111111', locale: 'en-US', currency: 'USD' }) },
        { method: 'GET', url: '/api/meter-regression-prompts/open', respond: () => jsonResponse(openPromptDto) },
      ])

      render(<App />)

      await screen.findByRole('dialog', { name: 'That reading is lower than the last one' })

      // Radix's modal Dialog marks the rest of the page aria-hidden while open — the trigger
      // becomes unreachable to assistive tech, which is what makes "supersedes rather than
      // stacks" (AC #7) a real accessibility guarantee, not just a visual one.
      expect(screen.queryByRole('button', { name: 'Log reading' })).not.toBeInTheDocument()
    })
  })
})
