import { render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { act } from 'react'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { getThemePreference, initColorScheme, setThemePreference } from '@/lib/color-scheme'
import { AppearanceRow } from './appearance-row'

function mockMatchMedia(matches: boolean) {
  const listeners: Array<() => void> = []
  const query = { matches, addEventListener: (_: string, cb: () => void) => listeners.push(cb) }
  vi.stubGlobal('matchMedia', vi.fn().mockReturnValue(query))
  return {
    fireChange: (next: boolean) => {
      query.matches = next
      listeners.forEach((cb) => cb())
    },
  }
}

const isDark = () => document.documentElement.classList.contains('dark')

describe('AppearanceRow', () => {
  let fetchMock: ReturnType<typeof vi.fn>

  beforeEach(() => {
    fetchMock = vi.fn()
    vi.stubGlobal('fetch', fetchMock)
  })

  afterEach(() => {
    document.documentElement.classList.remove('dark')
    vi.unstubAllGlobals()
    setThemePreference('system')
  })

  it('renders System, Light, Dark in order with the scope sub-label', () => {
    mockMatchMedia(false)
    initColorScheme()
    render(<AppearanceRow />)

    const group = screen.getByRole('radiogroup', { name: 'Appearance' })
    expect(group).toBeInTheDocument()
    expect(screen.getAllByRole('radio').map((r) => r.getAttribute('aria-label'))).toEqual([
      'System — currently Light',
      'Light',
      'Dark',
    ])
    expect(screen.getByText('This device')).toBeInTheDocument()
    expect(screen.getByRole('radio', { name: /^System/ })).toBeChecked()
  })

  it("System's accessible name follows the resolved OS value live", () => {
    const { fireChange } = mockMatchMedia(false)
    initColorScheme()
    render(<AppearanceRow />)
    expect(screen.getByRole('radio', { name: 'System — currently Light' })).toBeInTheDocument()

    act(() => fireChange(true))
    expect(screen.getByRole('radio', { name: 'System — currently Dark' })).toBeInTheDocument()
  })

  it("System's name reports the OS scheme, not the pinned theme, and still tracks OS changes", () => {
    const { fireChange } = mockMatchMedia(false)
    initColorScheme()
    setThemePreference('dark')
    render(<AppearanceRow />)
    expect(screen.getByRole('radio', { name: 'System — currently Light' })).toBeInTheDocument()

    act(() => fireChange(true))
    expect(screen.getByRole('radio', { name: 'System — currently Dark' })).toBeInTheDocument()
    expect(document.documentElement.classList.contains('dark')).toBe(true)
  })

  it('selecting Dark/Light applies immediately and updates the preference — with no network call', async () => {
    const user = userEvent.setup()
    Object.defineProperty(navigator, 'onLine', { value: false, configurable: true })
    mockMatchMedia(false)
    initColorScheme()
    render(<AppearanceRow />)

    await user.click(screen.getByRole('radio', { name: 'Dark' }))
    expect(isDark()).toBe(true)
    expect(getThemePreference()).toBe('dark')
    expect(screen.getByRole('radio', { name: 'Dark' })).toBeChecked()

    await user.click(screen.getByRole('radio', { name: 'Light' }))
    expect(isDark()).toBe(false)
    expect(getThemePreference()).toBe('light')

    await user.click(screen.getByRole('radio', { name: /^System/ }))
    expect(getThemePreference()).toBe('system')

    expect(fetchMock).not.toHaveBeenCalled()
    Object.defineProperty(navigator, 'onLine', { value: true, configurable: true })
  })

  it('reflects an existing choice on mount', () => {
    mockMatchMedia(false)
    initColorScheme()
    setThemePreference('dark')
    render(<AppearanceRow />)
    expect(screen.getByRole('radio', { name: 'Dark' })).toBeChecked()
  })
})
