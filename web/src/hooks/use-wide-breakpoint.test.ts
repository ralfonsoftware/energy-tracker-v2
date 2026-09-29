import { act, renderHook } from '@testing-library/react'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { useWideBreakpoint } from './use-wide-breakpoint'

function stubMatchMedia(initial: boolean) {
  let matches = initial
  const listeners = new Set<() => void>()
  const mql = {
    get matches() {
      return matches
    },
    media: '(min-width: 660px)',
    addEventListener: (_: string, cb: () => void) => listeners.add(cb),
    removeEventListener: (_: string, cb: () => void) => listeners.delete(cb),
  }
  const matchMedia = vi.fn(() => mql)
  vi.stubGlobal('matchMedia', matchMedia)
  return {
    matchMedia,
    set(value: boolean) {
      matches = value
      listeners.forEach((cb) => cb())
    },
    listenerCount: () => listeners.size,
  }
}

describe('useWideBreakpoint', () => {
  afterEach(() => {
    vi.unstubAllGlobals()
  })

  it('returns false when matchMedia is unavailable (jsdom)', () => {
    vi.stubGlobal('matchMedia', undefined)
    const { result } = renderHook(() => useWideBreakpoint())
    expect(result.current).toBe(false)
  })

  it('queries the 660px min-width and reflects the initial match', () => {
    const stub = stubMatchMedia(true)
    const { result } = renderHook(() => useWideBreakpoint())
    expect(stub.matchMedia).toHaveBeenCalledWith('(min-width: 660px)')
    expect(result.current).toBe(true)
  })

  it('flips when the media query changes', () => {
    const stub = stubMatchMedia(false)
    const { result } = renderHook(() => useWideBreakpoint())
    expect(result.current).toBe(false)
    act(() => stub.set(true))
    expect(result.current).toBe(true)
    act(() => stub.set(false))
    expect(result.current).toBe(false)
  })

  it('removes its change listener on unmount', () => {
    const stub = stubMatchMedia(false)
    const { unmount } = renderHook(() => useWideBreakpoint())
    expect(stub.listenerCount()).toBe(1)
    unmount()
    expect(stub.listenerCount()).toBe(0)
  })
})
