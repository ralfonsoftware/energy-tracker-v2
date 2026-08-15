import { afterEach, describe, expect, it, vi } from 'vitest'
import { initColorScheme } from './color-scheme'

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

afterEach(() => {
  document.documentElement.classList.remove('dark')
  vi.unstubAllGlobals()
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
