import { afterEach, describe, expect, it, vi } from 'vitest'
import {
  getResolvedTheme,
  getThemePreference,
  initColorScheme,
  setThemePreference,
  subscribeTheme,
  THEME_COLOR_DARK,
  THEME_COLOR_LIGHT,
  THEME_STORAGE_KEY,
} from './color-scheme'

function mockMatchMedia(matches: boolean) {
  const listeners: Array<() => void> = []
  const query = {
    matches,
    addEventListener: (_: string, cb: () => void) => listeners.push(cb),
  }
  vi.stubGlobal('matchMedia', vi.fn().mockReturnValue(query))
  return {
    query,
    fireChange: (nextMatches: boolean) => {
      query.matches = nextMatches
      listeners.forEach((cb) => cb())
    },
  }
}

const isDark = () => document.documentElement.classList.contains('dark')

afterEach(() => {
  document.documentElement.classList.remove('dark')
  document.head.querySelectorAll('meta[name="theme-color"]').forEach((m) => m.remove())
  window.localStorage.clear()
  vi.restoreAllMocks()
  vi.unstubAllGlobals()
  // Reset the module's in-memory fallback + cached preference.
  setThemePreference('system')
})

describe('initColorScheme', () => {
  it('applies the dark class when the OS prefers dark on load', () => {
    mockMatchMedia(true)
    initColorScheme()
    expect(document.documentElement.classList.contains('dark')).toBe(true)
  })

  it('leaves the dark class off when the OS prefers light on load', () => {
    mockMatchMedia(false)
    initColorScheme()
    expect(document.documentElement.classList.contains('dark')).toBe(false)
  })

  it('toggles the dark class live when the OS scheme changes', () => {
    const { fireChange } = mockMatchMedia(false)
    initColorScheme()
    expect(document.documentElement.classList.contains('dark')).toBe(false)

    fireChange(true)
    expect(document.documentElement.classList.contains('dark')).toBe(true)

    fireChange(false)
    expect(document.documentElement.classList.contains('dark')).toBe(false)
  })
})

describe('theme preference (Story 8.10, FR-35)', () => {
  it('defaults to system when nothing is stored', () => {
    mockMatchMedia(true)
    initColorScheme()
    expect(getThemePreference()).toBe('system')
    expect(getResolvedTheme()).toBe('dark')
  })

  it('stored light on a dark OS → no dark class', () => {
    window.localStorage.setItem(THEME_STORAGE_KEY, 'light')
    mockMatchMedia(true)
    initColorScheme()
    expect(isDark()).toBe(false)
    expect(getResolvedTheme()).toBe('light')
  })

  it('stored dark on a light OS → dark class', () => {
    window.localStorage.setItem(THEME_STORAGE_KEY, 'dark')
    mockMatchMedia(false)
    initColorScheme()
    expect(isDark()).toBe(true)
  })

  it('ignores OS changes while light or dark is chosen (AC #4)', () => {
    const { fireChange } = mockMatchMedia(false)
    initColorScheme()
    setThemePreference('light')
    fireChange(true)
    expect(isDark()).toBe(false)

    setThemePreference('dark')
    fireChange(false)
    expect(isDark()).toBe(true)
  })

  it('switching back to system re-syncs to the current OS value', () => {
    const { fireChange } = mockMatchMedia(false)
    initColorScheme()
    setThemePreference('light')
    fireChange(true)
    expect(isDark()).toBe(false)

    setThemePreference('system')
    expect(isDark()).toBe(true)
    fireChange(false)
    expect(isDark()).toBe(false)
  })

  it('setThemePreference persists light/dark and removes the key for system', () => {
    mockMatchMedia(false)
    initColorScheme()
    setThemePreference('dark')
    expect(window.localStorage.getItem(THEME_STORAGE_KEY)).toBe('dark')
    setThemePreference('system')
    expect(window.localStorage.getItem(THEME_STORAGE_KEY)).toBeNull()
  })

  it('treats an unrecognized stored value as system (AC #6)', () => {
    window.localStorage.setItem(THEME_STORAGE_KEY, 'purple')
    mockMatchMedia(true)
    initColorScheme()
    expect(getThemePreference()).toBe('system')
    expect(isDark()).toBe(true)
  })

  it('falls back to system when getItem throws (AC #6)', () => {
    vi.spyOn(Storage.prototype, 'getItem').mockImplementation(() => {
      throw new Error('denied')
    })
    mockMatchMedia(true)
    expect(() => initColorScheme()).not.toThrow()
    expect(getThemePreference()).toBe('system')
    expect(isDark()).toBe(true)
  })

  it('still applies the choice for the session when setItem throws', () => {
    vi.spyOn(Storage.prototype, 'setItem').mockImplementation(() => {
      throw new Error('quota')
    })
    mockMatchMedia(false)
    initColorScheme()
    expect(() => setThemePreference('dark')).not.toThrow()
    expect(isDark()).toBe(true)
    expect(getThemePreference()).toBe('dark')
  })

  it('keeps the theme-color meta in step with the effective theme', () => {
    const meta = document.createElement('meta')
    meta.name = 'theme-color'
    meta.content = THEME_COLOR_LIGHT
    document.head.appendChild(meta)

    const { fireChange } = mockMatchMedia(true)
    initColorScheme()
    expect(meta.content).toBe(THEME_COLOR_DARK)

    setThemePreference('light')
    expect(meta.content).toBe(THEME_COLOR_LIGHT)

    fireChange(false)
    expect(meta.content).toBe(THEME_COLOR_LIGHT)

    setThemePreference('dark')
    expect(meta.content).toBe(THEME_COLOR_DARK)
  })

  it('notifies subscribers on change', () => {
    mockMatchMedia(false)
    initColorScheme()
    const cb = vi.fn()
    const unsubscribe = subscribeTheme(cb)
    setThemePreference('dark')
    expect(cb).toHaveBeenCalled()
    cb.mockClear()
    unsubscribe()
    setThemePreference('light')
    expect(cb).not.toHaveBeenCalled()
  })

  it('re-applies when another tab changes the stored theme (storage event)', () => {
    mockMatchMedia(false)
    initColorScheme()
    expect(isDark()).toBe(false)

    window.localStorage.setItem(THEME_STORAGE_KEY, 'dark')
    window.dispatchEvent(new StorageEvent('storage', { key: THEME_STORAGE_KEY, newValue: 'dark' }))
    expect(isDark()).toBe(true)
    expect(getThemePreference()).toBe('dark')
  })

  it('ignores storage events for other keys', () => {
    mockMatchMedia(false)
    initColorScheme()
    setThemePreference('light')
    window.localStorage.setItem('other', 'x')
    window.localStorage.setItem(THEME_STORAGE_KEY, 'dark')
    window.dispatchEvent(new StorageEvent('storage', { key: 'other', newValue: 'x' }))
    // Cache untouched by the unrelated event.
    expect(getThemePreference()).toBe('light')
  })
})
