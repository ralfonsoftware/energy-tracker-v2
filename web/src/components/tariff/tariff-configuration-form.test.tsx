import { render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { TariffConfigurationForm } from './tariff-configuration-form'

function jsonResponse(body: object | null, status = 200) {
  return new Response(body === null ? null : JSON.stringify(body), { status })
}

describe('TariffConfigurationForm', () => {
  afterEach(() => {
    vi.unstubAllGlobals()
  })

  it('pre-fills Currency from the Household currency', () => {
    render(<TariffConfigurationForm householdCurrency="EUR" onCreated={vi.fn()} />)

    expect(screen.getByLabelText('Currency')).toHaveValue('EUR')
  })

  it('submitting valid fields calls createTariff and onCreated', async () => {
    const created = {
      id: '11111111-1111-1111-1111-111111111111',
      monthlyBaseFee: 12.5,
      pricePerKwh: 0.32,
      currency: 'EUR',
      contractStartDate: '2026-01-15T00:00:00.000Z',
      contractPeriodMonths: 12,
      version: 0,
      isCurrent: true,
      effectiveUntil: null,
      corrections: [],
    }
    const fetchMock = vi.fn(() => Promise.resolve(jsonResponse(created)))
    vi.stubGlobal('fetch', fetchMock)
    const user = userEvent.setup()
    const onCreated = vi.fn()

    render(<TariffConfigurationForm householdCurrency="EUR" onCreated={onCreated} />)

    await user.type(screen.getByLabelText('Monthly base fee'), '12.50')
    await user.type(screen.getByLabelText('Price per kWh'), '0.32')
    await user.type(screen.getByLabelText('Contract start date'), '2026-01-15')
    await user.click(screen.getByRole('button', { name: 'Save Tariff' }))

    expect(fetchMock).toHaveBeenCalledWith(
      '/api/tariffs',
      expect.objectContaining({
        method: 'POST',
        body: JSON.stringify({
          monthlyBaseFee: 12.5,
          pricePerKwh: 0.32,
          currency: 'EUR',
          contractStartDate: '2026-01-15T00:00:00.000Z',
          contractPeriodMonths: 12,
        }),
      }),
    )
    await vi.waitFor(() => expect(onCreated).toHaveBeenCalledWith(created))
  })

  it('the Save button stays disabled until every required field is filled', () => {
    render(<TariffConfigurationForm householdCurrency="" onCreated={vi.fn()} />)

    expect(screen.getByRole('button', { name: 'Save Tariff' })).toBeDisabled()
  })

  it('a validation error from the server is shown', async () => {
    vi.stubGlobal('fetch', vi.fn(() => Promise.resolve(new Response(JSON.stringify({ detail: 'Invalid currency' }), { status: 400 }))))
    const user = userEvent.setup()

    render(<TariffConfigurationForm householdCurrency="EUR" onCreated={vi.fn()} />)

    await user.type(screen.getByLabelText('Monthly base fee'), '12.50')
    await user.type(screen.getByLabelText('Price per kWh'), '0.32')
    await user.type(screen.getByLabelText('Contract start date'), '2026-01-15')
    await user.click(screen.getByRole('button', { name: 'Save Tariff' }))

    expect(await screen.findByText('Invalid currency')).toBeInTheDocument()
  })
})
