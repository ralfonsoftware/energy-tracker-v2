import { render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { AiPlausibilityForm } from './ai-plausibility-form'

function jsonResponse(body: object | null, status = 200) {
  return new Response(body === null ? null : JSON.stringify(body), { status })
}

// Routes fetch calls by (method, URL) pair — matches yearly-baseline-form.test.tsx's pattern.
function mockFetchRoutes(
  routes: Array<{ method: string; url: string; respond: (body: unknown) => Response }>,
) {
  vi.stubGlobal(
    'fetch',
    vi.fn((input: string | URL | Request, init?: RequestInit) => {
      const url = String(input)
      const method = (init?.method ?? 'GET').toUpperCase()
      const route = routes.find((r) => r.method === method && r.url === url)
      if (!route) {
        throw new Error(`Unmocked fetch: ${method} ${url}`)
      }

      const body = init?.body ? JSON.parse(init.body as string) : undefined
      return Promise.resolve(route.respond(body))
    }),
  )
}

const householdId = 'h1'
const url = `/api/households/${householdId}/ai-plausibility`

describe('AiPlausibilityForm', () => {
  afterEach(() => {
    vi.unstubAllGlobals()
  })

  it('shows the toggle off and the backend as unconfigured by default', async () => {
    mockFetchRoutes([
      { method: 'GET', url, respond: () => jsonResponse({ enabled: false, backendConfigured: false, backendLabel: null, version: 0 }) },
    ])

    render(<AiPlausibilityForm householdId={householdId} />)

    expect(await screen.findByRole('switch')).not.toBeChecked()
    expect(screen.getByText(/no ai backend is configured/i)).toBeInTheDocument()
  })

  it('shows the backend label when configured', async () => {
    mockFetchRoutes([
      { method: 'GET', url, respond: () => jsonResponse({ enabled: true, backendConfigured: true, backendLabel: 'Local (LMStudio)', version: 2 }) },
    ])

    render(<AiPlausibilityForm householdId={householdId} />)

    expect(await screen.findByRole('switch')).toBeChecked()
    expect(await screen.findByText(/Local \(LMStudio\)/)).toBeInTheDocument()
  })

  it('toggling on submits enabled true with the current version and updates the switch', async () => {
    mockFetchRoutes([
      { method: 'GET', url, respond: () => jsonResponse({ enabled: false, backendConfigured: true, backendLabel: 'OpenAI', version: 0 }) },
      {
        method: 'PUT',
        url,
        respond: (body) => {
          expect(body).toEqual({ enabled: true, version: 0 })
          return jsonResponse({ enabled: true, backendConfigured: true, backendLabel: 'OpenAI', version: 1 })
        },
      },
    ])
    const user = userEvent.setup()

    render(<AiPlausibilityForm householdId={householdId} />)
    await user.click(await screen.findByRole('switch'))

    expect(await screen.findByRole('switch')).toBeChecked()
  })

  it('a 409 response triggers a refetch and shows a conflict message', async () => {
    let getCallCount = 0
    mockFetchRoutes([
      {
        method: 'GET',
        url,
        respond: () => {
          getCallCount += 1
          return getCallCount === 1
            ? jsonResponse({ enabled: false, backendConfigured: false, backendLabel: null, version: 0 })
            : jsonResponse({ enabled: true, backendConfigured: false, backendLabel: null, version: 3 })
        },
      },
      {
        method: 'PUT',
        url,
        respond: () => jsonResponse({ detail: 'Household was updated by someone else.' }, 409),
      },
    ])
    const user = userEvent.setup()

    render(<AiPlausibilityForm householdId={householdId} />)
    await user.click(await screen.findByRole('switch'))

    expect(await screen.findByText(/changed elsewhere/i)).toBeInTheDocument()
    expect(await screen.findByRole('switch')).toBeChecked()
  })
})
