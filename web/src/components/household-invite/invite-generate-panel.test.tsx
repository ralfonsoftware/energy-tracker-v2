import { render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { InviteGeneratePanel } from './invite-generate-panel'

function stubInviteFetch() {
  vi.stubGlobal(
    'fetch',
    vi.fn(() => Promise.resolve(new Response(JSON.stringify({ token: 'abc123', expiresAtUtc: '2026-10-17T00:00:00Z' }), { status: 200 }))),
  )
}

describe('InviteGeneratePanel', () => {
  afterEach(() => {
    vi.unstubAllGlobals()
  })

  it('renders inside its own glass card by default (Settings keeps this look)', () => {
    const { container } = render(<InviteGeneratePanel />)

    expect(container.querySelectorAll('[data-slot="glass-card"]')).toHaveLength(1)
  })

  // Story 11.1 Task 7.4 (Sally's option B): inside another card (the restore result) the panel
  // renders flat, without a second glass card, so there is no card-in-card.
  it('renders without a glass card when bare, and still generates a link', async () => {
    stubInviteFetch()
    const { container } = render(<InviteGeneratePanel bare />)

    expect(container.querySelectorAll('[data-slot="glass-card"]')).toHaveLength(0)

    await userEvent.click(screen.getByRole('button', { name: 'Invite a member' }))

    expect(await screen.findByLabelText('Invite link')).toHaveValue(`${window.location.origin}/join/abc123`)
    expect(container.querySelectorAll('[data-slot="glass-card"]')).toHaveLength(0)
  })
})
