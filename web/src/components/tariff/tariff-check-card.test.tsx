import { render, screen } from '@testing-library/react'
import { describe, expect, it, vi } from 'vitest'
import { TariffCheckCard } from './tariff-check-card'
import type { TariffCheckReminderDto } from '@/lib/tariff-check-api'

describe('TariffCheckCard', () => {
  it('renders nothing when reminder is null', () => {
    const { container } = render(<TariffCheckCard reminder={null} locale="en-US" />)

    expect(container).toBeEmptyDOMElement()
  })

  it('renders the due copy without a fabricated "since you last checked" claim', () => {
    const reminder: TariffCheckReminderDto = { isDue: true, gateOpensAtUtc: '2026-06-01T00:00:00+00:00' }

    render(<TariffCheckCard reminder={reminder} locale="en-US" />)

    expect(screen.getByText(/worth a look/i)).toBeInTheDocument()
    expect(screen.queryByText(/since you last/i)).not.toBeInTheDocument()
  })

  it('renders the not-due copy with the formatted gateOpensAtUtc date', () => {
    const reminder: TariffCheckReminderDto = { isDue: false, gateOpensAtUtc: '2026-06-01T00:00:00+00:00' }

    render(<TariffCheckCard reminder={reminder} locale="en-US" />)

    expect(screen.getByText(/nothing due right now/i)).toBeInTheDocument()
    expect(screen.getByText(/Jun 1, 2026/)).toBeInTheDocument()
  })

  it('fires onClick when tapped as an interactive button, when provided', () => {
    const reminder: TariffCheckReminderDto = { isDue: true, gateOpensAtUtc: '2026-06-01T00:00:00+00:00' }
    const onClick = vi.fn()

    render(<TariffCheckCard reminder={reminder} locale="en-US" onClick={onClick} />)
    screen.getByRole('button').click()

    expect(onClick).toHaveBeenCalledTimes(1)
  })

  it('renders as a non-interactive block when onClick is omitted', () => {
    const reminder: TariffCheckReminderDto = { isDue: true, gateOpensAtUtc: '2026-06-01T00:00:00+00:00' }

    render(<TariffCheckCard reminder={reminder} locale="en-US" />)

    expect(screen.queryByRole('button')).not.toBeInTheDocument()
  })
})
