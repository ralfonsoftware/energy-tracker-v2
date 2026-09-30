import 'fake-indexeddb/auto'
import { render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { IDBFactory } from 'fake-indexeddb'
import { setThemePreference } from '@/lib/color-scheme'
import { HouseholdLocaleContext } from '@/lib/household-locale-context'
import { ProfileMenu } from './profile-menu'

const householdId = '11111111-1111-1111-1111-111111111111'

function setOnLine(value: boolean) {
  Object.defineProperty(navigator, 'onLine', { configurable: true, value })
}

function renderWithLocale() {
  return render(
    <HouseholdLocaleContext.Provider value={{ locale: 'en-US', setLocale: () => {} }}>
      <ProfileMenu email="ralf@example.com" householdId={householdId} supportsFederatedLogout={true} />
    </HouseholdLocaleContext.Provider>,
  )
}

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
    setThemePreference('system')
    setOnLine(true)
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

  describe('Appearance row (Story 8.10)', () => {
    it('sits between the email and the Profile row, inside a 296px-wide menu (AC #1)', async () => {
      const user = userEvent.setup()
      render(<ProfileMenu email="ralf@example.com" householdId={householdId} supportsFederatedLogout={true} />)
      await user.click(screen.getByRole('button', { name: 'Account menu' }))

      const email = await screen.findByText('ralf@example.com')
      const group = screen.getByRole('radiogroup', { name: 'Appearance' })
      const profile = screen.getByText('Profile')
      expect(email.compareDocumentPosition(group) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy()
      expect(group.compareDocumentPosition(profile) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy()
      expect(screen.getByRole('menu')).toHaveClass('w-[296px]')
      expect(within(group).getAllByRole('radio')).toHaveLength(3)
    })

    it('selecting a segment keeps the menu open (AC #3)', async () => {
      const user = userEvent.setup()
      render(<ProfileMenu email="ralf@example.com" householdId={householdId} supportsFederatedLogout={true} />)
      await user.click(screen.getByRole('button', { name: 'Account menu' }))

      await user.click(await screen.findByRole('radio', { name: 'Dark' }))

      expect(screen.getByRole('radio', { name: 'Dark' })).toBeChecked()
      expect(screen.getByText('Profile')).toBeInTheDocument()
      expect(screen.getByText('Log off')).toBeInTheDocument()
    })

    it('arrow keys change the selection without Radix stealing focus to a menu item (AC #2)', async () => {
      const user = userEvent.setup()
      render(<ProfileMenu email="ralf@example.com" householdId={householdId} supportsFederatedLogout={true} />)
      await user.click(screen.getByRole('button', { name: 'Account menu' }))

      const system = await screen.findByRole('radio', { name: /^System/ })
      system.focus()
      await user.keyboard('{ArrowRight}')
      expect(screen.getByRole('radio', { name: 'Light' })).toHaveFocus()
      expect(screen.getByRole('radio', { name: 'Light' })).toBeChecked()

      await user.keyboard('{End}')
      expect(screen.getByRole('radio', { name: 'Dark' })).toHaveFocus()

      await user.keyboard('{Home}')
      expect(screen.getByRole('radio', { name: /^System/ })).toHaveFocus()
    })

    it('Tab and Shift+Tab cycle Appearance → Language → first enabled menu item (AC #2, 8.11 two-strip revisit)', async () => {
      const user = userEvent.setup()
      renderWithLocale()
      await user.click(screen.getByRole('button', { name: 'Account menu' }))
      await screen.findByRole('radiogroup', { name: 'Appearance' })

      screen.getByRole('menuitem', { name: 'Log off' }).focus()
      await user.tab()
      expect(screen.getByRole('radio', { name: /^System/ })).toHaveFocus()

      await user.tab()
      expect(screen.getByRole('radio', { name: 'English' })).toHaveFocus()

      await user.tab()
      expect(screen.getByRole('menuitem', { name: 'Log off' })).toHaveFocus()

      await user.tab({ shift: true })
      expect(screen.getByRole('radio', { name: 'English' })).toHaveFocus()

      await user.tab({ shift: true })
      expect(screen.getByRole('radio', { name: /^System/ })).toHaveFocus()
    })

    it('Tab skips the disabled (offline) Language strip', async () => {
      setOnLine(false)
      const user = userEvent.setup()
      renderWithLocale()
      await user.click(screen.getByRole('button', { name: 'Account menu' }))
      await screen.findByRole('radiogroup', { name: 'Appearance' })

      screen.getByRole('radio', { name: /^System/ }).focus()
      await user.tab()
      expect(screen.getByRole('menuitem', { name: 'Log off' })).toHaveFocus()
    })

    it('renders the Language row below Appearance and above Profile (8.11, AC #1)', async () => {
      const user = userEvent.setup()
      renderWithLocale()
      await user.click(screen.getByRole('button', { name: 'Account menu' }))

      const appearance = await screen.findByRole('radiogroup', { name: 'Appearance' })
      const language = screen.getByRole('radiogroup', { name: 'Language' })
      const profile = screen.getByText('Profile')
      expect(appearance.compareDocumentPosition(language) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy()
      expect(language.compareDocumentPosition(profile) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy()
    })

    it('selecting a language keeps the menu open, including after the save resolves', async () => {
      vi.stubGlobal('fetch', vi.fn().mockResolvedValue(jsonResponse({})))
      const user = userEvent.setup()
      renderWithLocale()
      await user.click(screen.getByRole('button', { name: 'Account menu' }))

      await user.click(await screen.findByRole('radio', { name: 'Deutsch' }))

      await waitFor(() => expect(screen.getByTestId('profile-menu-live-region')).toHaveTextContent('Sprache: Deutsch'))
      expect(screen.getByRole('menu')).toBeInTheDocument()
    })

    it('Escape closes the menu and returns focus to the avatar button (AC #2)', async () => {
      const user = userEvent.setup()
      render(<ProfileMenu email="ralf@example.com" householdId={householdId} supportsFederatedLogout={true} />)
      const trigger = screen.getByRole('button', { name: 'Account menu' })
      await user.click(trigger)

      const light = await screen.findByRole('radio', { name: 'Light' })
      light.focus()
      await user.keyboard('{Escape}')

      await waitFor(() => expect(screen.queryByRole('menu')).not.toBeInTheDocument())
      expect(trigger).toHaveFocus()
    })
  })
})
