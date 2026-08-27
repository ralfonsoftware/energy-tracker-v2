import { render, screen } from '@testing-library/react'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { SettingsPage } from './settings-page'

function jsonResponse(body: object | null, status = 200) {
  return new Response(body === null ? null : JSON.stringify(body), { status })
}

const householdId = '11111111-1111-1111-1111-111111111111'

describe('SettingsPage', () => {
  afterEach(() => {
    vi.unstubAllGlobals()
  })

  it('no longer renders a Smart Plug Import panel — moved to its own Dashboard-launched screen (Story 3.5, AC #1)', async () => {
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
        return Promise.resolve(jsonResponse(null))
      }),
    )

    render(<SettingsPage householdId={householdId} onBack={() => {}} />)

    expect(await screen.findByRole('heading', { name: 'Settings' })).toBeInTheDocument()
    expect(screen.queryByText('Smart Plug Import')).not.toBeInTheDocument()
    expect(screen.queryByText('Drop a file here, or choose one to upload.')).not.toBeInTheDocument()
  })
})
