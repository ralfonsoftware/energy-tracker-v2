// Theme preference: System | Light | Dark, stored per device (FR-35) — never on the Household.
// System (the default, stored as "nothing") follows the OS color scheme live; Light/Dark override it
// and ignore OS changes. DESIGN.md treats Dark and Light as equal citizens.
// index.html carries a synchronous inline copy of the read logic (same key and values as
// getThemePreference) applied before first paint to avoid a flash of the wrong theme — the two
// MUST stay in sync. This module keeps things live after mount.
// This file (and its test) is the single allowlisted localStorage user — see
// FrontendDoesNotStoreAuthTokensTests. The stored value is only 'light' | 'dark', never a token.

export type ThemePreference = 'system' | 'light' | 'dark'
export type ResolvedTheme = 'light' | 'dark'

export const THEME_STORAGE_KEY = 'energy-tracker-theme'
// Same literals as the inline script in index.html (duplicated there by necessity).
export const THEME_COLOR_LIGHT = '#F3F8ED'
export const THEME_COLOR_DARK = '#12201A'

const DARK_QUERY = '(prefers-color-scheme: dark)'

// null = not read yet. Also the in-memory fallback when storage is unavailable.
let current: ThemePreference | null = null
const subscribers = new Set<() => void>()
let listenersAttached = false

function parse(value: string | null): ThemePreference {
  return value === 'light' || value === 'dark' ? value : 'system'
}

function readStored(): ThemePreference {
  try {
    return parse(window.localStorage.getItem(THEME_STORAGE_KEY))
  } catch {
    return 'system'
  }
}

function osPrefersDark(): boolean {
  return typeof window !== 'undefined' && typeof window.matchMedia === 'function'
    ? window.matchMedia(DARK_QUERY).matches
    : false
}

export function getThemePreference(): ThemePreference {
  current ??= readStored()
  return current
}

export function getResolvedTheme(): ResolvedTheme {
  const preference = getThemePreference()
  return preference === 'system' ? (osPrefersDark() ? 'dark' : 'light') : preference
}

function syncThemeColorMeta(resolved: ResolvedTheme): void {
  const meta = document.querySelector('meta[name="theme-color"]')
  meta?.setAttribute('content', resolved === 'dark' ? THEME_COLOR_DARK : THEME_COLOR_LIGHT)
}

function apply(): void {
  const resolved = getResolvedTheme()
  document.documentElement.classList.toggle('dark', resolved === 'dark')
  syncThemeColorMeta(resolved)
  subscribers.forEach((cb) => cb())
}

export function subscribeTheme(cb: () => void): () => void {
  subscribers.add(cb)
  return () => {
    subscribers.delete(cb)
  }
}

export function setThemePreference(preference: ThemePreference): void {
  current = preference
  try {
    if (preference === 'system') {
      window.localStorage.removeItem(THEME_STORAGE_KEY)
    } else {
      window.localStorage.setItem(THEME_STORAGE_KEY, preference)
    }
  } catch {
    // Storage unavailable (e.g. Safari private mode): the choice still applies for this session.
  }
  apply()
}

export function initColorScheme(): void {
  current = readStored()
  apply()

  if (typeof window.matchMedia === 'function') {
    // Preference is read at event time: OS changes only matter while it is 'system'.
    window.matchMedia(DARK_QUERY).addEventListener('change', () => {
      if (getThemePreference() === 'system') {
        apply()
      }
    })
  }

  if (!listenersAttached) {
    listenersAttached = true
    // Another tab on the same device changed the theme.
    window.addEventListener('storage', (event) => {
      if (event.key !== null && event.key !== THEME_STORAGE_KEY) {
        return
      }
      current = readStored()
      apply()
    })
  }
}
