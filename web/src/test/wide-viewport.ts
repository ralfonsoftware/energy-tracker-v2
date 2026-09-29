import { vi } from 'vitest'

// jsdom has no matchMedia; useWideBreakpoint treats that as narrow. Call vi.unstubAllGlobals() to undo.
export function stubWideViewport() {
  vi.stubGlobal('matchMedia', (query: string) => ({
    matches: query === '(min-width: 660px)',
    addEventListener: () => {},
    removeEventListener: () => {},
  }))
}
