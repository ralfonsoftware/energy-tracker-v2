import 'fake-indexeddb/auto'
import { render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { IDBFactory } from 'fake-indexeddb'
import { ProfileMenu } from './profile-menu'

const householdId = '11111111-1111-1111-1111-111111111111'

function jsonResponse(body: object | null, status = 200) {
  return new Response(body === null ? null : JSON.stringify(body), { status })
}

function stubLocation() {
  const originalLocation = window.location
  const mockLocation = { ...originalLocation, href: '' }
  Object.defineProperty(window, 'location', { value: mockLocation, writable: true })
  return () => Object.defineProperty(window, 'location', { value: originalLocation, writable: true })
}

// Radix's floating-menu primitives (DropdownMenu, like the existing Select) call
// hasPointerCapture/scrollIntoView, neither implemented by jsdom — the same gap this codebase's
// other Radix-floating-primitive consumers work around.
beforeEach(() => {
  Element.prototype.hasPointerCapture ??= () => false
  Element.prototype.scrollIntoView ??= () => {}
})

describe('ProfileMenu', () => {
  beforeEach(() => {
    globalThis.indexedDB = new IDBFactory()
  })

  afterEach(() => {
    vi.unstubAllGlobals()
  })

  it('opens on click and shows the account email, Profile, and Log off rows (AC #3)', async () => {
    const user = userEvent.setup()
    render(<ProfileMenu email="ralf@example.com" householdId={householdId} supportsFederatedLogout={true} />)

    await user.click(screen.getByRole('button', { name: 'Account menu' }))

    expect(await screen.findByText('ralf@example.com')).toBeInTheDocument()
    expect(screen.getByText('Profile')).toBeInTheDocument()
    expect(screen.getByText('Log off')).toBeInTheDocument()
  })

  it('renders the Profile row as present but non-interactive — no destination is defined for it yet', async () => {
    const user = userEvent.setup()
    render(<ProfileMenu email="ralf@example.com" householdId={householdId} supportsFederatedLogout={true} />)

    await user.click(screen.getByRole('button', { name: 'Account menu' }))

    expect(await screen.findByText('Profile')).toHaveAttribute('data-disabled')
  })

  it('omits the email row entirely when no email claim was resolved, rather than a blank row', async () => {
    const user = userEvent.setup()
    render(<ProfileMenu email={null} householdId={householdId} supportsFederatedLogout={true} />)

    await user.click(screen.getByRole('button', { name: 'Account menu' }))

    expect(await screen.findByText('Profile')).toBeInTheDocument()
    expect(screen.queryByText('@')).not.toBeInTheDocument()
  })

  it('clicking Log off drives the shared use-logoff hook — the same flow as the Settings entry point (AC #4)', async () => {
    const user = userEvent.setup()
    vi.stubGlobal('fetch', vi.fn(() => Promise.resolve(jsonResponse(null))))
    const restoreLocation = stubLocation()
    render(<ProfileMenu email="ralf@example.com" householdId={householdId} supportsFederatedLogout={true} />)

    await user.click(screen.getByRole('button', { name: 'Account menu' }))
    await user.click(await screen.findByText('Log off'))

    expect(await screen.findByRole('heading', { name: 'Log off?' })).toBeInTheDocument()

    await user.click(screen.getByRole('button', { name: 'Yes, log off' }))
    await waitFor(() => expect(window.location.href).toBe('/logout'))
    restoreLocation()
  })
})
