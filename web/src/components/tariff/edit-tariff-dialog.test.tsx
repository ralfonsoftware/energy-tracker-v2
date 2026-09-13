import { render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { EditTariffDialog } from './edit-tariff-dialog'
import type { TariffHistoryItemDto } from '@/lib/tariff-api'

function jsonResponse(body: object | null, status = 200) {
  return new Response(body === null ? null : JSON.stringify(body), { status })
}

// Computed relative to the real clock rather than faked — avoids userEvent's own internal timers
// hanging against vi.useFakeTimers() (a known interaction, not something worth fighting here).
const ONE_DAY_MS = 24 * 60 * 60 * 1000
const FUTURE_DATE = new Date(Date.now() + 30 * ONE_DAY_MS).toISOString()
const PAST_DATE = new Date(Date.now() - 30 * ONE_DAY_MS).toISOString()

function tariff(overrides: Partial<TariffHistoryItemDto> = {}): TariffHistoryItemDto {
  return {
    id: '11111111-1111-1111-1111-111111111111',
    monthlyBaseFee: 12.5,
    pricePerKwh: 0.32,
    currency: 'EUR',
    contractStartDate: FUTURE_DATE,
    contractPeriodMonths: 12,
    version: 3,
    isCurrent: true,
    effectiveUntil: null,
    corrections: [],
    ...overrides,
  }
}

describe('EditTariffDialog', () => {
  afterEach(() => {
    vi.unstubAllGlobals()
  })

  it('a future-dated entry saves with a single Save click — no override step shown', async () => {
    const futureTariff = tariff({ contractStartDate: FUTURE_DATE })
    const fetchMock = vi.fn(() => Promise.resolve(jsonResponse({ ...futureTariff, monthlyBaseFee: 20, version: 4 })))
    vi.stubGlobal('fetch', fetchMock)
    const user = userEvent.setup()
    const onSaved = vi.fn()

    render(<EditTariffDialog tariff={futureTariff} open={true} onOpenChange={() => {}} onSaved={onSaved} />)

    expect(screen.queryByText(/already started/)).not.toBeInTheDocument()

    const input = screen.getByLabelText('Monthly base fee')
    await user.clear(input)
    await user.type(input, '20')
    await user.click(screen.getByRole('button', { name: 'Save' }))

    expect(fetchMock).toHaveBeenCalledWith(
      `/api/tariffs/${futureTariff.id}`,
      expect.objectContaining({
        method: 'PUT',
        body: JSON.stringify({
          monthlyBaseFee: 20,
          pricePerKwh: futureTariff.pricePerKwh,
          currency: futureTariff.currency,
          contractPeriodMonths: futureTariff.contractPeriodMonths,
          version: futureTariff.version,
          overrideConfirmed: false,
        }),
      }),
    )
    await vi.waitFor(() => expect(onSaved).toHaveBeenCalledOnce())
  })

  it('editing a price field on an already-started entry requires the override checkbox before Save is enabled', async () => {
    const pastTariff = tariff({ contractStartDate: PAST_DATE })
    const user = userEvent.setup()

    render(<EditTariffDialog tariff={pastTariff} open={true} onOpenChange={() => {}} onSaved={vi.fn()} />)

    expect(screen.getByText(/already started/)).toBeInTheDocument()

    const input = screen.getByLabelText('Monthly base fee')
    await user.clear(input)
    await user.type(input, '20')

    expect(screen.getByRole('button', { name: 'Save' })).toBeDisabled()

    await user.click(screen.getByRole('checkbox'))

    expect(screen.getByRole('button', { name: 'Save' })).not.toBeDisabled()
  })

  it('confirming the override sends overrideConfirmed true', async () => {
    const pastTariff = tariff({ contractStartDate: PAST_DATE })
    const fetchMock = vi.fn(() => Promise.resolve(jsonResponse({ ...pastTariff, monthlyBaseFee: 20, version: 4 })))
    vi.stubGlobal('fetch', fetchMock)
    const user = userEvent.setup()
    const onSaved = vi.fn()

    render(<EditTariffDialog tariff={pastTariff} open={true} onOpenChange={() => {}} onSaved={onSaved} />)

    const input = screen.getByLabelText('Monthly base fee')
    await user.clear(input)
    await user.type(input, '20')
    await user.click(screen.getByRole('checkbox'))
    await user.click(screen.getByRole('button', { name: 'Save' }))

    expect(fetchMock).toHaveBeenCalledWith(
      `/api/tariffs/${pastTariff.id}`,
      expect.objectContaining({
        body: expect.stringContaining('"overrideConfirmed":true'),
      }),
    )
    await vi.waitFor(() => expect(onSaved).toHaveBeenCalledOnce())
  })

  it('editing only Currency on an already-started entry needs no override step', async () => {
    const pastTariff = tariff({ contractStartDate: PAST_DATE })
    const fetchMock = vi.fn(() => Promise.resolve(jsonResponse({ ...pastTariff, currency: 'USD', version: 4 })))
    vi.stubGlobal('fetch', fetchMock)
    const user = userEvent.setup()

    render(<EditTariffDialog tariff={pastTariff} open={true} onOpenChange={() => {}} onSaved={vi.fn()} />)

    const currencyInput = screen.getByLabelText('Currency')
    await user.clear(currencyInput)
    await user.type(currencyInput, 'USD')

    expect(screen.queryByRole('checkbox')).not.toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Save' })).not.toBeDisabled()
  })

  it('moving an already-started entrys ContractStartDate forward (unedited price) still requires the override checkbox', async () => {
    // Closes the bypass where moving a locked entry's date into the future — to make a later
    // price edit appear unlocked — was never itself gated.
    const pastTariff = tariff({ contractStartDate: PAST_DATE })
    const user = userEvent.setup()
    const newDate = new Date(Date.now() + 60 * ONE_DAY_MS).toISOString().slice(0, 10)

    render(<EditTariffDialog tariff={pastTariff} open={true} onOpenChange={() => {}} onSaved={vi.fn()} />)

    const dateInput = screen.getByLabelText('Contract start date')
    await user.clear(dateInput)
    await user.type(dateInput, newDate)

    expect(screen.getByRole('button', { name: 'Save' })).toBeDisabled()
    expect(screen.getByRole('checkbox')).toBeInTheDocument()
  })

  it('changing ContractStartDate sends it, and omits it entirely when left untouched', async () => {
    const futureTariff = tariff({ contractStartDate: FUTURE_DATE })
    const fetchMock = vi.fn(() => Promise.resolve(jsonResponse({ ...futureTariff, version: 4 })))
    vi.stubGlobal('fetch', fetchMock)
    const user = userEvent.setup()
    const onSaved = vi.fn()
    const newDate = new Date(Date.now() + 90 * ONE_DAY_MS).toISOString().slice(0, 10)

    render(<EditTariffDialog tariff={futureTariff} open={true} onOpenChange={() => {}} onSaved={onSaved} />)

    const dateInput = screen.getByLabelText('Contract start date')
    await user.clear(dateInput)
    await user.type(dateInput, newDate)
    await user.click(screen.getByRole('button', { name: 'Save' }))

    expect(fetchMock).toHaveBeenCalledWith(
      `/api/tariffs/${futureTariff.id}`,
      expect.objectContaining({
        body: expect.stringContaining(`"contractStartDate":"${new Date(newDate).toISOString()}"`),
      }),
    )
    await vi.waitFor(() => expect(onSaved).toHaveBeenCalledOnce())
  })

  it('a 409 conflict shows the conflict message and does not call onSaved', async () => {
    vi.stubGlobal('fetch', vi.fn(() => Promise.resolve(new Response(JSON.stringify({ detail: 'stale' }), { status: 409 }))))
    const user = userEvent.setup()
    const onSaved = vi.fn()
    const futureTariff = tariff({ contractStartDate: FUTURE_DATE })

    render(<EditTariffDialog tariff={futureTariff} open={true} onOpenChange={() => {}} onSaved={onSaved} />)

    await user.click(screen.getByRole('button', { name: 'Save' }))

    expect(await screen.findByText('This Tariff entry was changed elsewhere — refresh and try again.')).toBeInTheDocument()
    expect(onSaved).not.toHaveBeenCalled()
  })

  it('a non-409 error shows the generic error message', async () => {
    vi.stubGlobal('fetch', vi.fn(() => Promise.resolve(new Response(null, { status: 500 }))))
    const user = userEvent.setup()
    const futureTariff = tariff({ contractStartDate: FUTURE_DATE })

    render(<EditTariffDialog tariff={futureTariff} open={true} onOpenChange={() => {}} onSaved={vi.fn()} />)

    await user.click(screen.getByRole('button', { name: 'Save' }))

    expect(await screen.findByText('Something went wrong. Please try again.')).toBeInTheDocument()
  })
})
