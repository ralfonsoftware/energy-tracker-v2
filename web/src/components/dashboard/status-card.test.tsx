import { render, screen } from '@testing-library/react'
import { describe, expect, it } from 'vitest'
import { StatusCard } from './status-card'
import type { StatusDto } from '@/lib/status-api'

function dto(overrides: Partial<StatusDto> = {}): StatusDto {
  return {
    status: 'withinRange',
    paceToDateKwh: 1000,
    baselineToDateKwh: 1000,
    isLowConfidence: false,
    ...overrides,
  }
}

describe('StatusCard', () => {
  it('renders the within-range state with headline, badge and a named-number supporting sentence', () => {
    render(<StatusCard status={dto({ status: 'withinRange', paceToDateKwh: 1060, baselineToDateKwh: 1300 })} loading={false} locale="en-US" playEntranceAnimation={true} />)

    expect(screen.getByText('Quiet week.')).toBeInTheDocument()
    expect(screen.getByText('Within range')).toBeInTheDocument()
    expect(screen.getByText('240 kWh under pace.')).toBeInTheDocument()
  })

  it('renders the below-baseline state', () => {
    render(<StatusCard status={dto({ status: 'belowBaseline', paceToDateKwh: 800, baselineToDateKwh: 980 })} loading={false} locale="en-US" playEntranceAnimation={true} />)

    expect(screen.getByText('Well under baseline.')).toBeInTheDocument()
    expect(screen.getByText('Below baseline')).toBeInTheDocument()
    expect(screen.getByText('180 kWh under pace.')).toBeInTheDocument()
  })

  it('renders the trending state', () => {
    render(<StatusCard status={dto({ status: 'trending', paceToDateKwh: 1450, baselineToDateKwh: 1300 })} loading={false} locale="en-US" playEntranceAnimation={true} />)

    expect(screen.getByText('Worth a look.')).toBeInTheDocument()
    expect(screen.getByText('Trending')).toBeInTheDocument()
    expect(screen.getByText('150 kWh over pace.')).toBeInTheDocument()
  })

  it('shows "Right on pace." when pace exactly equals baseline-to-date', () => {
    render(<StatusCard status={dto({ status: 'withinRange', paceToDateKwh: 1000, baselineToDateKwh: 1000 })} loading={false} locale="en-US" playEntranceAnimation={true} />)

    expect(screen.getByText('Right on pace.')).toBeInTheDocument()
  })

  it('never shows "Right on pace." when the badge/dot says belowBaseline, even if the difference rounds to zero', () => {
    // Backend's ResolveStatus classifies BelowBaseline on any unrounded-negative difference
    // (PatternDetectiveCalculator.cs: "difference < 0m"). -0.3 rounds to 0 — if the sentence
    // branched on the rounded value it would say "Right on pace." while the badge/dot still say
    // "Below baseline", a visible contradiction between the two.
    render(
      <StatusCard
        status={dto({ status: 'belowBaseline', paceToDateKwh: 999.7, baselineToDateKwh: 1000 })}
        loading={false}
        locale="en-US"
        playEntranceAnimation={true}
      />,
    )

    expect(screen.getByText('Below baseline')).toBeInTheDocument()
    expect(screen.queryByText('Right on pace.')).not.toBeInTheDocument()
    expect(screen.getByText('0 kWh under pace.')).toBeInTheDocument()
  })

  it('does not apply the entrance/specular-sweep animation classes when playEntranceAnimation is false', () => {
    const { container } = render(
      <StatusCard status={dto()} loading={false} locale="en-US" playEntranceAnimation={false} />,
    )

    expect(container.querySelector('.motion-safe\\:animate-status-card-entrance')).toBeNull()
    expect(container.querySelector('.motion-safe\\:animate-status-card-specular-sweep')).toBeNull()
  })

  it('formats the kWh figure using the given locale', () => {
    render(<StatusCard status={dto({ status: 'trending', paceToDateKwh: 12450, baselineToDateKwh: 1300 })} loading={false} locale="de-DE" playEntranceAnimation={true} />)

    // de-DE uses a period/non-breaking-space grouping separator, not a comma.
    expect(screen.getByText(/11\.150/)).toBeInTheDocument()
  })

  it('renders an additional low-confidence note when isLowConfidence is true', () => {
    render(<StatusCard status={dto({ isLowConfidence: true })} loading={false} locale="en-US" playEntranceAnimation={true} />)

    expect(screen.getByText(/It's been a while since your last reading/)).toBeInTheDocument()
  })

  it('does not render a low-confidence note when isLowConfidence is false', () => {
    render(<StatusCard status={dto({ isLowConfidence: false })} loading={false} locale="en-US" playEntranceAnimation={true} />)

    expect(screen.queryByText(/It's been a while since your last reading/)).not.toBeInTheDocument()
  })

  it('announces the status content via an aria-live="polite" region', () => {
    render(<StatusCard status={dto()} loading={false} locale="en-US" playEntranceAnimation={true} />)

    const region = screen.getByText('Right on pace.').closest('[aria-live="polite"]')
    expect(region).not.toBeNull()
  })

  it('uses the dedicated badge-text token class on the badge, never the raw status-triad class', () => {
    render(<StatusCard status={dto({ status: 'withinRange' })} loading={false} locale="en-US" playEntranceAnimation={true} />)

    const badge = screen.getByText('Within range')
    expect(badge).toHaveClass('text-status-within-range-badge-text')
    expect(badge).not.toHaveClass('text-status-within-range')
  })

  it('shows a skeleton matching the card footprint while loading, instead of a real or empty state', () => {
    render(<StatusCard status={null} loading={true} locale="en-US" playEntranceAnimation={true} />)

    expect(screen.getByTestId('status-card-skeleton')).toBeInTheDocument()
    expect(screen.queryByText('No Status yet')).not.toBeInTheDocument()
  })

  it('shows the onboarding empty state when Status is null and not loading', () => {
    render(<StatusCard status={null} loading={false} locale="en-US" playEntranceAnimation={true} />)

    expect(screen.getByText('No Status yet')).toBeInTheDocument()
    expect(
      screen.getByText('Log your first reading to get started — Pattern Detective needs at least two to find your pace.'),
    ).toBeInTheDocument()
  })

  it('renders the given emptyStateAction inside the onboarding empty state', () => {
    render(
      <StatusCard status={null} loading={false} locale="en-US" playEntranceAnimation={true} emptyStateAction={<button>Log reading</button>} />,
    )

    expect(screen.getByRole('button', { name: 'Log reading' })).toBeInTheDocument()
  })

  it('renders detailTrigger only in the populated state, not while loading or empty', () => {
    const { rerender } = render(
      <StatusCard
        status={dto()}
        loading={false}
        locale="en-US"
        playEntranceAnimation={true}
        detailTrigger={<button>How was this calculated?</button>}
      />,
    )
    expect(screen.getByRole('button', { name: 'How was this calculated?' })).toBeInTheDocument()

    rerender(
      <StatusCard
        status={null}
        loading={true}
        locale="en-US"
        playEntranceAnimation={true}
        detailTrigger={<button>How was this calculated?</button>}
      />,
    )
    expect(screen.queryByRole('button', { name: 'How was this calculated?' })).not.toBeInTheDocument()

    rerender(
      <StatusCard
        status={null}
        loading={false}
        locale="en-US"
        playEntranceAnimation={true}
        detailTrigger={<button>How was this calculated?</button>}
      />,
    )
    expect(screen.queryByRole('button', { name: 'How was this calculated?' })).not.toBeInTheDocument()
  })
})
