import { render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it, vi } from 'vitest'
import { UnitInput } from './unit-input'

describe('UnitInput', () => {
  it('renders the unit text inside the same field as the number, not a separate label', () => {
    render(<UnitInput aria-label="Meter reading" unit="kWh" type="number" value={100} onChange={() => {}} />)

    expect(screen.getByText('kWh')).toBeInTheDocument()
    expect(screen.getByRole('spinbutton', { name: 'Meter reading' })).toHaveValue(100)
  })

  it('forwards value and onChange to the underlying input', async () => {
    const handleChange = vi.fn()
    const user = userEvent.setup()
    render(<UnitInput aria-label="Meter reading" unit="kWh" type="number" value="" onChange={handleChange} />)

    await user.type(screen.getByRole('spinbutton', { name: 'Meter reading' }), '5')

    expect(handleChange).toHaveBeenCalled()
  })

  it('forwards className to the underlying input and wrapperClassName to the wrapper', () => {
    render(
      <UnitInput
        aria-label="Meter reading"
        unit="kWh"
        type="number"
        value={100}
        onChange={() => {}}
        className="text-red-500"
        wrapperClassName="max-w-xs"
      />
    )

    expect(screen.getByRole('spinbutton', { name: 'Meter reading' })).toHaveClass('text-red-500')
    expect(screen.getByRole('spinbutton', { name: 'Meter reading' }).closest('[data-slot="unit-input-wrap"]')).toHaveClass('max-w-xs')
  })
})
