import { useState } from 'react'
import { act, render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import i18next from 'i18next'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { getThemePreference, setThemePreference } from '@/lib/color-scheme'
import { HouseholdLocaleContext } from '@/lib/household-locale-context'
import { ProfileMenu } from '@/components/dashboard/profile-menu'
import { PreferencesCard } from './preferences-card'

function setOnLine(value: boolean) {
  Object.defineProperty(navigator, 'onLine', { configurable: true, value })
}

function renderCard(locale: string | null = 'en-US', setLocale = vi.fn()) {
  render(
    <HouseholdLocaleContext.Provider value={{ locale, setLocale }}>
      <PreferencesCard householdId="h-1" />
    </HouseholdLocaleContext.Provider>,
  )
  return { setLocale }
}

function Harness() {
  const [locale, setLocale] = useState<string | null>('en-US')
  return (
    <HouseholdLocaleContext.Provider value={{ locale, setLocale }}>
      <PreferencesCard householdId="h-1" />
      <ProfileMenu email="a@b.c" householdId="h-1" supportsFederatedLogout={false} />
    </HouseholdLocaleContext.Provider>
  )
}

describe('PreferencesCard', () => {
  afterEach(async () => {
    vi.unstubAllGlobals()
    setOnLine(true)
    setThemePreference('system')
    document.documentElement.classList.remove('dark')
    await i18next.changeLanguage('en-US')
  })

  it('renders the Appearance then Language radiogroups with their scope sub-labels', () => {
    renderCard()
    const groups = screen.getAllByRole('radiogroup')
    expect(groups.map((g) => g.getAttribute('data-testid'))).toEqual(['appearance-strip', 'language-strip'])
    expect(screen.getByRole('radiogroup', { name: 'Appearance' })).toBeInTheDocument()
    expect(screen.getByRole('radiogroup', { name: 'Language' })).toBeInTheDocument()
    expect(screen.getByText('This device')).toBeInTheDocument()
    expect(screen.getByText('Whole household')).toBeInTheDocument()
    expect(screen.getByTestId('preferences-card')).toBeInTheDocument()
  })

  it('applies a theme selection immediately and persists it per device', async () => {
    const user = userEvent.setup()
    renderCard()
    await user.click(screen.getByRole('radio', { name: 'Dark' }))
    expect(screen.getByRole('radio', { name: 'Dark' })).toBeChecked()
    expect(getThemePreference()).toBe('dark')
    expect(document.documentElement.classList.contains('dark')).toBe(true)
  })

  it('runs the language save flow and announces in the card’s own live region', async () => {
    const fetchMock = vi.fn().mockResolvedValue(new Response('{}', { status: 200 }))
    vi.stubGlobal('fetch', fetchMock)
    const user = userEvent.setup()
    const { setLocale } = renderCard('en-US')

    await user.click(screen.getByRole('radio', { name: 'Deutsch' }))

    expect(fetchMock).toHaveBeenCalledWith(
      '/api/households/h-1/locale',
      expect.objectContaining({ method: 'PUT', body: JSON.stringify({ locale: 'de-DE' }) }),
    )
    await waitFor(() => expect(setLocale).toHaveBeenCalledWith('de-DE'))
    await waitFor(() => expect(screen.getByRole('status')).toHaveTextContent('Sprache: Deutsch'))
    expect(screen.getByRole('status')).toHaveAttribute('aria-live', 'polite')
  })

  it('reverts the strip and shows an alert when the save fails', async () => {
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(new Response(null, { status: 500 })))
    const user = userEvent.setup()
    const { setLocale } = renderCard('en-US')

    await user.click(screen.getByRole('radio', { name: 'Deutsch' }))

    expect(await screen.findByRole('alert')).toBeInTheDocument()
    expect(screen.getByRole('radio', { name: 'English' })).toBeChecked()
    expect(setLocale).not.toHaveBeenCalled()
  })

  it('disables Language offline with the sub-label while Appearance stays usable', async () => {
    const user = userEvent.setup()
    renderCard()
    setOnLine(false)
    act(() => {
      window.dispatchEvent(new Event('offline'))
    })

    expect(await screen.findByText('Needs a connection')).toBeInTheDocument()
    screen.getAllByRole('radio', { name: /Deutsch|English/ }).forEach((r) => expect(r).toBeDisabled())
    await user.click(screen.getByRole('radio', { name: 'Light' }))
    expect(screen.getByRole('radio', { name: 'Light' })).toBeChecked()
  })

  it('shares persisted state with the Profile menu (AC #4): theme and locale set in the card show there', async () => {
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(new Response('{}', { status: 200 })))
    const user = userEvent.setup()
    render(<Harness />)

    await user.click(screen.getByRole('radio', { name: 'Dark' }))
    await user.click(screen.getByRole('radio', { name: 'Deutsch' }))
    await waitFor(() => expect(screen.getByRole('radio', { name: 'Deutsch' })).not.toHaveAttribute('aria-busy'))

    await user.click(screen.getByRole('button', { name: /Account menu|Konto/ }))
    const menu = await screen.findByRole('menu')
    expect(within(menu).getByTestId('appearance-strip').querySelector('[aria-checked="true"]')).toHaveAccessibleName('Dark')
    expect(within(menu).getByTestId('language-strip').querySelector('[aria-checked="true"]')).toHaveAccessibleName('Deutsch')
  })
})
