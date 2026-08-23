// Branch on the *unrounded* sign, matching the backend's own ResolveStatus boundary
// (PatternDetectiveCalculator.cs: "difference < 0m" is BelowBaseline, otherwise WithinRange
// unless it clears the Trending threshold). Rounding the difference before branching could
// flip the displayed direction for any unrounded value in (-0.5, 0) — e.g. -0.3 rounds to 0
// and would render "Right on pace." while the badge/dot still say "Below baseline", a visible
// contradiction between the two. Only the displayed *magnitude* is rounded. Extracted here
// (Story 2.7) so status-card.tsx and status-detail-dialog.tsx share a single source of truth
// rather than risk reintroducing that exact bug class if the two copies ever drift.
export function computeStatusDifference(
  paceToDateKwh: number,
  baselineToDateKwh: number,
): { sign: 'under' | 'over' | 'on'; roundedMagnitude: number; rawDifference: number } {
  const rawDifference = paceToDateKwh - baselineToDateKwh
  const roundedMagnitude = Math.round(Math.abs(rawDifference))

  let sign: 'under' | 'over' | 'on'
  if (rawDifference === 0) {
    sign = 'on'
  } else if (rawDifference < 0) {
    sign = 'under'
  } else {
    sign = 'over'
  }

  return { sign, roundedMagnitude, rawDifference }
}
