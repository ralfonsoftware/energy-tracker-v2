import { render, screen } from '@testing-library/react'
import { describe, expect, it } from 'vitest'
import { GapCard } from './gap-card'
import type { SmartPlugImportGapDto } from '@/lib/smart-plug-import-api'

describe('GapCard', () => {
  it('renders the estimated treatment with the daily average and day count', () => {
    const gap: SmartPlugImportGapDto = {
      startDate: '2026-04-12',
      endDate: '2026-04-17',
      treatment: 'estimated',
      estimatedTotalKwh: 24.6,
    }

    render(<GapCard gap={gap} />)

    expect(screen.getByText('Estimated')).toBeInTheDocument()
    expect(screen.getByText(/No data received for 6 days/)).toBeInTheDocument()
    expect(screen.getByText(/4\.1 kWh\/day/)).toBeInTheDocument()
  })

  it('renders the missing treatment without a daily average', () => {
    const gap: SmartPlugImportGapDto = {
      startDate: '2026-08-01',
      endDate: '2026-08-03',
      treatment: 'missing',
      estimatedTotalKwh: null,
    }

    render(<GapCard gap={gap} />)

    expect(screen.getByText('Missing, not estimated')).toBeInTheDocument()
    expect(screen.getByText(/No data received for 3 days/)).toBeInTheDocument()
    expect(screen.getByText(/left unfilled and flagged as missing/)).toBeInTheDocument()
  })

  it('renders the flagged-for-review treatment', () => {
    const gap: SmartPlugImportGapDto = {
      startDate: '2026-08-01',
      endDate: '2026-08-09',
      treatment: 'flaggedforreview',
      estimatedTotalKwh: null,
    }

    render(<GapCard gap={gap} />)

    expect(screen.getByText('Flagged for review')).toBeInTheDocument()
    expect(screen.getByText(/came back with no interval data/)).toBeInTheDocument()
  })

  it('renders a single date without a range dash when start and end match', () => {
    const gap: SmartPlugImportGapDto = {
      startDate: '2026-08-01',
      endDate: '2026-08-01',
      treatment: 'flaggedforreview',
      estimatedTotalKwh: null,
    }

    render(<GapCard gap={gap} />)

    expect(screen.queryByText(/–/)).not.toBeInTheDocument()
  })
})
