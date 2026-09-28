import { render, screen, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { InviteMemberRow } from './invite-member-row'

function jsonResponse(body: object | null, status = 200) {
  return new Response(body === null ? null : JSON.stringify(body), { status })
}

describe('InviteMemberRow', () => {
  afterEach(() => {
    vi.unstubAllGlobals()
  })

  // Component-test reality, not a claim about visual exclusivity (that's the e2e/live-verification
  // job) — jsdom applies neither `wide:hidden` nor `hidden wide:block`, so both the narrow-mode
  // InviteGeneratePanel and the wide-mode row trigger are simultaneously present in the render.
  it('renders both the narrow-mode InviteGeneratePanel and the wide-mode row trigger', () => {
    render(<InviteMemberRow />)

    // Narrow-mode's standalone generate button plus the wide-mode row's own label — same
    // accessible name, two matches.
    expect(screen.getAllByRole('button', { name: 'Invite a member' })).toHaveLength(2)
  })

  it('clicking the wide-mode row opens a Dialog containing a working InviteGeneratePanel', async () => {
    const user = userEvent.setup()
    vi.stubGlobal(
      'fetch',
      vi.fn((input: string | URL | Request) => {
        const url = String(input)
        if (url === '/api/household-invites') {
          return Promise.resolve(jsonResponse({ token: 'abcd1234', expiresAtUtc: '2026-08-21T00:00:00Z' }))
        }
        return Promise.resolve(jsonResponse(null))
      }),
    )

    render(<InviteMemberRow />)

    const [, wideModeButton] = screen.getAllByRole('button', { name: 'Invite a member' })
    await user.click(wideModeButton)

    const dialog = await screen.findByRole('dialog')
    // The dialog now contains a third "Invite a member" generate button (its own mounted
    // InviteGeneratePanel instance) — scope to the dialog to avoid ambiguity with the two
    // already-present narrow/wide triggers.
    const dialogGenerateButton = within(dialog).getByRole('button', { name: 'Invite a member' })
    await user.click(dialogGenerateButton)

    const linkInput = await within(dialog).findByLabelText('Invite link')
    expect(linkInput).toHaveValue(`${window.location.origin}/join/abcd1234`)
  })
})
