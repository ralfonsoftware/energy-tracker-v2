import { describe, expect, it } from 'vitest'
import { computeStatusDifference } from './status-difference'

describe('computeStatusDifference', () => {
  it('reports "on" with zero magnitude when pace exactly equals baseline-to-date', () => {
    const result = computeStatusDifference(1000, 1000)

    expect(result).toEqual({ sign: 'on', roundedMagnitude: 0, rawDifference: 0 })
  })

  it('reports "under" with the rounded magnitude when pace is below baseline-to-date', () => {
    const result = computeStatusDifference(800, 980)

    expect(result.sign).toBe('under')
    expect(result.roundedMagnitude).toBe(180)
    expect(result.rawDifference).toBe(-180)
  })

  it('reports "over" with the rounded magnitude when pace is above baseline-to-date', () => {
    const result = computeStatusDifference(1450, 1300)

    expect(result.sign).toBe('over')
    expect(result.roundedMagnitude).toBe(150)
    expect(result.rawDifference).toBe(150)
  })

  it('reports "under" with a zero rounded magnitude for a near-zero negative difference — never "on"', () => {
    // Regression case (Story 2.5 review fix): a raw difference like -0.3 must report sign
    // "under" so the caller never contradicts the backend's ResolveStatus boundary
    // ("difference < 0m" is BelowBaseline), even though the rounded magnitude displays as 0.
    const result = computeStatusDifference(999.7, 1000)

    expect(result.sign).toBe('under')
    expect(result.roundedMagnitude).toBe(0)
    expect(result.rawDifference).toBeCloseTo(-0.3)
  })
})
