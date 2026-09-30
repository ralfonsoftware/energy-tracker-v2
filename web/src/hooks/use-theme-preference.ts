import { useSyncExternalStore } from 'react'
import {
  getResolvedTheme,
  getThemePreference,
  setThemePreference,
  subscribeTheme,
  type ResolvedTheme,
  type ThemePreference,
} from '@/lib/color-scheme'

interface ThemePreferenceState {
  preference: ThemePreference
  resolved: ResolvedTheme
  setPreference: (preference: ThemePreference) => void
}

// Two separate snapshots (both primitive strings) keep useSyncExternalStore's referential check stable.
export function useThemePreference(): ThemePreferenceState {
  const preference = useSyncExternalStore(subscribeTheme, getThemePreference, () => 'system' as const)
  const resolved = useSyncExternalStore(subscribeTheme, getResolvedTheme, () => 'light' as const)
  return { preference, resolved, setPreference: setThemePreference }
}
