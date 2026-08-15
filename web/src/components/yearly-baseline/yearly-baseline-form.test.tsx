import { render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { YearlyBaselineForm } from './yearly-baseline-form'

function jsonResponse(body: object | null, status = 200) {
  return new Response(body === null ? null : JSON.stringify(body), { status })
}

// Routes fetch calls by (method, URL) pair — matches tagging-scaffold-manager.test.tsx's pattern.
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
const getUrl = `/api/households/${householdId}`
const putUrl = `/api/households/${householdId}/yearly-baseline`

describe('YearlyBaselineForm', () => {
  afterEach(() => {
    vi.unstubAllGlobals()
  })

  it('clicking a preset fills the input without submitting', async () => {
    mockFetchRoutes([
      { method: 'GET', url: getUrl, respond: () => jsonResponse({ id: householdId, locale: 'en-US', currency: 'USD', yearlyBaselineKwh: null, version: 0 }) },
    ])
    const user = userEvent.setup()

    render(<YearlyBaselineForm householdId={householdId} />)
    await screen.findByLabelText('Yearly Baseline (kWh)')

    await user.click(screen.getByRole('button', { name: /3500/ }))

    expect(screen.getByLabelText('Yearly Baseline (kWh)')).toHaveValue(3500)
    // No PUT call was made — only the mocked GET route exists, so any PUT would throw.
  })

  it('a successful submit updates the shown value and Version', async () => {
    mockFetchRoutes([
      { method: 'GET', url: getUrl, respond: () => jsonResponse({ id: householdId, locale: 'en-US', currency: 'USD', yearlyBaselineKwh: null, version: 0 }) },
      {
        method: 'PUT',
        url: putUrl,
        respond: (body) => {
          expect(body).toEqual({ yearlyBaselineKwh: 2500, version: 0 })
          return jsonResponse({ id: householdId, locale: 'en-US', currency: 'USD', yearlyBaselineKwh: 2500, version: 1 })
        },
      },
    ])
    const user = userEvent.setup()

    render(<YearlyBaselineForm householdId={householdId} />)
    await user.click(await screen.findByRole('button', { name: /2500/ }))
    await user.click(screen.getByRole('button', { name: 'Save' }))

    expect(await screen.findByLabelText('Yearly Baseline (kWh)')).toHaveValue(2500)
  })

  it('a 409 response triggers a refetch and shows a conflict message', async () => {
    let getCallCount = 0
    mockFetchRoutes([
      {
        method: 'GET',
        url: getUrl,
        respond: () => {
          getCallCount += 1
          return getCallCount === 1
            ? jsonResponse({ id: householdId, locale: 'en-US', currency: 'USD', yearlyBaselineKwh: null, version: 0 })
            : jsonResponse({ id: householdId, locale: 'en-US', currency: 'USD', yearlyBaselineKwh: 4250, version: 3 })
        },
      },
      {
        method: 'PUT',
        url: putUrl,
        respond: () => jsonResponse({ detail: 'Household was updated by someone else.' }, 409),
      },
    ])
    const user = userEvent.setup()

    render(<YearlyBaselineForm householdId={householdId} />)
    await user.click(await screen.findByRole('button', { name: /1500/ }))
    await user.click(screen.getByRole('button', { name: 'Save' }))

    expect(await screen.findByText(/changed elsewhere/i)).toBeInTheDocument()
    expect(await screen.findByLabelText('Yearly Baseline (kWh)')).toHaveValue(4250)
  })
})
