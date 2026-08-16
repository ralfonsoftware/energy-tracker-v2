import { render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it, vi } from 'vitest'
import { HouseholdSizePresetRow, PRESETS } from './household-size-preset-row'

describe('HouseholdSizePresetRow', () => {
  it('renders 4 buttons with the full preset sentence as their accessible name', () => {
    render(<HouseholdSizePresetRow presets={PRESETS} selectedKwh={null} onSelect={() => {}} />)

    expect(screen.getAllByRole('button')).toHaveLength(4)
    expect(screen.getByRole('button', { name: /1 person.*1500/i })).toBeInTheDocument()
    expect(screen.getByRole('button', { name: /4 people.*4250/i })).toBeInTheDocument()
  })

  it('calls onSelect with the right kWh value on click', async () => {
    const handleSelect = vi.fn()
    const user = userEvent.setup()
    render(<HouseholdSizePresetRow presets={PRESETS} selectedKwh={null} onSelect={handleSelect} />)

    await user.click(screen.getByRole('button', { name: /2500/ }))

    expect(handleSelect).toHaveBeenCalledTimes(1)
    expect(handleSelect).toHaveBeenCalledWith(2500)
  })

  it('never auto-submits anything — every preset is a plain type="button"', () => {
    render(<HouseholdSizePresetRow presets={PRESETS} selectedKwh={2500} onSelect={() => {}} />)

    for (const button of screen.getAllByRole('button')) {
      expect(button).toHaveAttribute('type', 'button')
    }
  })
})
