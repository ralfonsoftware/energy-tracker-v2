import { useSyncExternalStore } from 'react'
import {
  getSystemTheme,
  getThemePreference,
  setThemePreference,
  subscribeTheme,
  type ResolvedTheme,
  type ThemePreference,
} from '@/lib/color-scheme'

interface ThemePreferenceState {
  preference: ThemePreference
  // What System resolves to now (the OS scheme) — not the applied theme when Light/Dark is pinned.
  system: ResolvedTheme
  setPreference: (preference: ThemePreference) => void
}

// Two separate snapshots (both primitive strings) keep useSyncExternalStore's referential check stable.
export function useThemePreference(): ThemePreferenceState {
  const preference = useSyncExternalStore(subscribeTheme, getThemePreference, () => 'system' as const)
  const system = useSyncExternalStore(subscribeTheme, getSystemTheme, () => 'light' as const)
  return { preference, system, setPreference: setThemePreference }
}
