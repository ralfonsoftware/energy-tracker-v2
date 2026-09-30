import { act, render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import i18next from 'i18next'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { HouseholdLocaleContext } from '@/lib/household-locale-context'
import { LanguageRow } from './language-row'

function setOnLine(value: boolean) {
  Object.defineProperty(navigator, 'onLine', { configurable: true, value })
}

function renderRow(locale: string | null, setLocale = vi.fn(), onAnnounce = vi.fn()) {
  render(
    <HouseholdLocaleContext.Provider value={{ locale, setLocale }}>
      <LanguageRow householdId="h-1" onAnnounce={onAnnounce} />
    </HouseholdLocaleContext.Provider>,
  )
  return { setLocale, onAnnounce }
}

describe('LanguageRow', () => {
  afterEach(async () => {
    vi.unstubAllGlobals()
    setOnLine(true)
    await i18next.changeLanguage('en-US')
  })

  it('offers DE/EN with endonym names, the scope sub-label and the persisted locale checked', () => {
    renderRow('de-DE')
    const strip = screen.getByRole('radiogroup', { name: 'Language' })
    expect(strip).toBe(screen.getByTestId('language-strip'))
    expect(screen.getByRole('radio', { name: 'Deutsch' })).toBeChecked()
    expect(screen.getByRole('radio', { name: 'Deutsch' })).toHaveTextContent('DE')
    expect(screen.getByRole('radio', { name: 'English' })).not.toBeChecked()
    expect(screen.getByText('Whole household')).toBeInTheDocument()
  })

  it('renders safely with nothing checked when there is no provider value', () => {
    renderRow(null)
    expect(screen.getAllByRole('radio').every((r) => r.getAttribute('aria-checked') === 'false')).toBe(true)
  })

  it('shows the selection immediately with a spinner, PUTs once, then sets the locale and announces in the new language', async () => {
    let resolve!: (r: Response) => void
    const fetchMock = vi.fn().mockReturnValue(new Promise<Response>((r) => (resolve = r)))
    vi.stubGlobal('fetch', fetchMock)
    const user = userEvent.setup()
    const { setLocale, onAnnounce } = renderRow('en-US')

    await user.click(screen.getByRole('radio', { name: 'Deutsch' }))

    expect(screen.getByRole('radio', { name: 'Deutsch' })).toBeChecked()
    expect(screen.getByTestId('language-strip')).toHaveAttribute('aria-busy', 'true')
    expect(screen.getByTestId('preference-strip-spinner')).toBeInTheDocument()
    expect(fetchMock).toHaveBeenCalledTimes(1)
    expect(fetchMock).toHaveBeenCalledWith(
      '/api/households/h-1/locale',
      expect.objectContaining({ method: 'PUT', body: JSON.stringify({ locale: 'de-DE' }) }),
    )

    // second click while pending is ignored
    await user.click(screen.getByRole('radio', { name: 'English' }))
    expect(fetchMock).toHaveBeenCalledTimes(1)

    await act(async () => resolve(new Response('{}', { status: 200 })))

    await waitFor(() => expect(setLocale).toHaveBeenCalledWith('de-DE'))
    expect(onAnnounce).toHaveBeenCalledWith('Sprache: Deutsch')
    expect(screen.getByTestId('language-strip')).not.toHaveAttribute('aria-busy')
  })

  it.each([
    ['a 500 response', () => vi.fn().mockResolvedValue(new Response(null, { status: 500 }))],
    ['a network error', () => vi.fn().mockRejectedValue(new TypeError('Failed to fetch'))],
  ])('reverts and shows an inline error in the persisted language on %s', async (_name, makeFetch) => {
    vi.stubGlobal('fetch', makeFetch())
    const user = userEvent.setup()
    const { setLocale, onAnnounce } = renderRow('en-US')

    await user.click(screen.getByRole('radio', { name: 'Deutsch' }))

    const alert = await screen.findByRole('alert')
    expect(alert).toHaveTextContent("Couldn't change the language. Your household is still on English")
    expect(screen.getByRole('radio', { name: 'English' })).toBeChecked()
    expect(setLocale).not.toHaveBeenCalled()
    expect(onAnnounce).not.toHaveBeenCalled()
    expect(i18next.language).toBe('en-US')
  })

  it('is disabled with a "Needs a connection" sub-label while offline and recovers on online', () => {
    setOnLine(false)
    renderRow('en-US')
    expect(screen.getByText('Needs a connection')).toBeInTheDocument()
    expect(screen.getAllByRole('radio').every((r) => (r as HTMLButtonElement).disabled)).toBe(true)

    act(() => {
      setOnLine(true)
      window.dispatchEvent(new Event('online'))
    })
    expect(screen.getByText('Whole household')).toBeInTheDocument()
    expect(screen.getAllByRole('radio').some((r) => (r as HTMLButtonElement).disabled)).toBe(false)
  })
})
