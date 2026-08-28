import { describe, expect, it } from 'vitest'
import { formatRelativeTime } from './format-relative-time'

describe('formatRelativeTime', () => {
  const now = new Date('2026-08-09T12:00:00Z')

  it('renders the exact same instant as "now"', () => {
    expect(formatRelativeTime('2026-08-09T12:00:00Z', 'en-US', now)).toBe('now')
  })

  it('renders a moment 2 days ago', () => {
    expect(formatRelativeTime('2026-08-07T12:00:00Z', 'en-US', now)).toBe('2 days ago')
  })

  it('renders a moment 30 seconds ago in seconds, below the minute threshold', () => {
    expect(formatRelativeTime('2026-08-09T11:59:30Z', 'en-US', now)).toBe('30 seconds ago')
  })

  it('is locale-aware', () => {
    expect(formatRelativeTime('2026-08-07T12:00:00Z', 'de-DE', now)).toBe('vorgestern')
  })

  it('renders "now" instead of a future tense for ordinary clock skew a few seconds ahead', () => {
    // Review-round-2 patch: `iso` is meant to always be in the past, but server/client clock
    // drift can put it fractionally ahead — must clamp to "now", never "in N seconds".
    expect(formatRelativeTime('2026-08-09T12:00:05Z', 'en-US', now)).toBe('now')
  })
})
