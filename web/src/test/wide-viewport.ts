import { vi } from 'vitest'

// jsdom has no matchMedia; useWideBreakpoint treats that as narrow. Call vi.unstubAllGlobals() to undo.
export function stubWideViewport() {
  vi.stubGlobal('matchMedia', () => ({
    matches: true,
    addEventListener: () => {},
    removeEventListener: () => {},
  }))
}
