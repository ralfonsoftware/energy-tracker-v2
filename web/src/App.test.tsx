import { render, screen } from '@testing-library/react'
import { afterEach, describe, expect, it, vi } from 'vitest'
import App from './App'

function mockSession(response: object | null, status = 200) {
  vi.stubGlobal(
    'fetch',
    vi.fn().mockResolvedValue(
      new Response(response === null ? null : JSON.stringify(response), { status }),
    ),
  )
}

describe('App', () => {
  afterEach(() => {
    vi.unstubAllGlobals()
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
})
