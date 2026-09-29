import { useSyncExternalStore } from 'react'

// Must match --breakpoint-wide (web/src/index.css) — the CSS variable can't be read at runtime.
const WIDE_QUERY = '(min-width: 660px)'

function hasMatchMedia() {
  return typeof window !== 'undefined' && typeof window.matchMedia === 'function'
}

function subscribe(onChange: () => void) {
  if (!hasMatchMedia()) {
    return () => {}
  }
  const mql = window.matchMedia(WIDE_QUERY)
  mql.addEventListener('change', onChange)
  return () => mql.removeEventListener('change', onChange)
}

function getSnapshot() {
  // jsdom has no matchMedia; false keeps the narrow (table) layout in unit tests.
  return hasMatchMedia() ? window.matchMedia(WIDE_QUERY).matches : false
}

export function useWideBreakpoint(): boolean {
  return useSyncExternalStore(subscribe, getSnapshot, () => false)
}
