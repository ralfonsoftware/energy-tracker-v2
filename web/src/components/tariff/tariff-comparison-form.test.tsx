import { render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { TariffComparisonForm } from './tariff-comparison-form'

function jsonResponse(body: object | null, status = 200) {
  return new Response(body === null ? null : JSON.stringify(body), { status })
}

describe('TariffComparisonForm', () => {
  afterEach(() => {
    vi.unstubAllGlobals()
  })

  it('a valid comparison renders the bonus-normalized savings figure with 2 decimal places', async () => {
    // Guard against Story 5.1's own review-found minimumFractionDigits bug recurring here — a
    // whole-euro value like 12.80 rendering as "12.8" would violate NFR6's fixed-decimal display.
    const comparison = {
      currentMonthlyBaseFee: 12.5,
      currentPricePerKwh: 0.32,
      currency: 'EUR',
      candidateMonthlyBaseFee: 14.9,
      candidatePricePerKwh: 0.315,
      candidateSwitchingBonus: 350,
      annualPaceKwh: 3200,
      currentAnnualCost: 1174,
      candidateAnnualCostBonusNormalized: 1186.8,
      bonusNormalizedAnnualSavings: -12.8,
    }
    vi.stubGlobal('fetch', vi.fn(() => Promise.resolve(jsonResponse(comparison))))
    const user = userEvent.setup()

    render(<TariffComparisonForm currency="EUR" locale="en-US" />)

    await user.type(screen.getByLabelText('Candidate monthly base fee'), '14.90')
    await user.type(screen.getByLabelText('Candidate price per kWh'), '0.3150')
    await user.type(screen.getByLabelText('Switching bonus (optional)'), '350')
    await user.click(screen.getByRole('button', { name: 'Compare' }))

    expect(await screen.findByText('This candidate would cost about 12.80 EUR/yr more, bonus-normalized.')).toBeInTheDocument()
    expect(screen.getByText('Based on your actual pace of 3,200 kWh/yr.')).toBeInTheDocument()
  })

  it('a null result (no pace yet) renders the same onboarding empty state as the Dashboard Status card', async () => {
    vi.stubGlobal('fetch', vi.fn(() => Promise.resolve(jsonResponse(null))))
    const user = userEvent.setup()

    render(<TariffComparisonForm currency="EUR" locale="en-US" />)

    await user.type(screen.getByLabelText('Candidate monthly base fee'), '14.90')
    await user.type(screen.getByLabelText('Candidate price per kWh'), '0.3150')
    await user.click(screen.getByRole('button', { name: 'Compare' }))

    expect(await screen.findByText('No Status yet')).toBeInTheDocument()
    expect(screen.getByText('Log your first reading to get started — Pattern Detective needs at least two to find your pace.')).toBeInTheDocument()
  })

  it('the switching bonus defaults to 0 when left blank', async () => {
    const fetchMock = vi.fn<typeof fetch>(() => Promise.resolve(jsonResponse(null)))
    vi.stubGlobal('fetch', fetchMock)
    const user = userEvent.setup()

    render(<TariffComparisonForm currency="EUR" locale="en-US" />)

    await user.type(screen.getByLabelText('Candidate monthly base fee'), '14.90')
    await user.type(screen.getByLabelText('Candidate price per kWh'), '0.3150')
    await user.click(screen.getByRole('button', { name: 'Compare' }))

    expect(fetchMock).toHaveBeenCalledWith('/api/tariffs/compare', expect.objectContaining({ method: 'POST' }))
    const requestInit = fetchMock.mock.calls[0][1]!
    expect(JSON.parse(requestInit.body as string)).toEqual({
      candidateMonthlyBaseFee: 14.9,
      candidatePricePerKwh: 0.315,
      candidateSwitchingBonus: 0,
    })
  })

  it('a stale result is cleared when a resubmission fails', async () => {
    const comparison = {
      currentMonthlyBaseFee: 12.5,
      currentPricePerKwh: 0.32,
      currency: 'EUR',
      candidateMonthlyBaseFee: 14.9,
      candidatePricePerKwh: 0.315,
      candidateSwitchingBonus: 0,
      annualPaceKwh: 3200,
      currentAnnualCost: 1174,
      candidateAnnualCostBonusNormalized: 1186.8,
      bonusNormalizedAnnualSavings: -12.8,
      isLowConfidence: false,
    }
    const fetchMock = vi
      .fn()
      .mockResolvedValueOnce(jsonResponse(comparison))
      .mockResolvedValueOnce(new Response(JSON.stringify({ detail: 'Invalid candidate' }), { status: 400 }))
    vi.stubGlobal('fetch', fetchMock)
    const user = userEvent.setup()

    render(<TariffComparisonForm currency="EUR" locale="en-US" />)
    await user.type(screen.getByLabelText('Candidate monthly base fee'), '14.90')
    await user.type(screen.getByLabelText('Candidate price per kWh'), '0.3150')
    await user.click(screen.getByRole('button', { name: 'Compare' }))
    expect(await screen.findByText('This candidate would cost about 12.80 EUR/yr more, bonus-normalized.')).toBeInTheDocument()

    await user.click(screen.getByRole('button', { name: 'Compare' }))

    expect(await screen.findByText('Invalid candidate')).toBeInTheDocument()
    expect(screen.queryByText('This candidate would cost about 12.80 EUR/yr more, bonus-normalized.')).not.toBeInTheDocument()
  })

  it('a low-confidence result shows the stale-pace footnote', async () => {
    const comparison = {
      currentMonthlyBaseFee: 12.5,
      currentPricePerKwh: 0.32,
      currency: 'EUR',
      candidateMonthlyBaseFee: 14.9,
      candidatePricePerKwh: 0.315,
      candidateSwitchingBonus: 0,
      annualPaceKwh: 3200,
      currentAnnualCost: 1174,
      candidateAnnualCostBonusNormalized: 1186.8,
      bonusNormalizedAnnualSavings: -12.8,
      isLowConfidence: true,
    }
    vi.stubGlobal('fetch', vi.fn(() => Promise.resolve(jsonResponse(comparison))))
    const user = userEvent.setup()

    render(<TariffComparisonForm currency="EUR" locale="en-US" />)
    await user.type(screen.getByLabelText('Candidate monthly base fee'), '14.90')
    await user.type(screen.getByLabelText('Candidate price per kWh'), '0.3150')
    await user.click(screen.getByRole('button', { name: 'Compare' }))

    expect(await screen.findByText("It's been a while since your last reading, so this pace may be less reliable than usual.")).toBeInTheDocument()
  })

  it('the Compare button stays disabled until the required fields are filled', () => {
    render(<TariffComparisonForm currency="EUR" locale="en-US" />)

    expect(screen.getByRole('button', { name: 'Compare' })).toBeDisabled()
  })

  it('the Compare button stays disabled with a negative switching bonus', async () => {
    const user = userEvent.setup()
    render(<TariffComparisonForm currency="EUR" locale="en-US" />)

    await user.type(screen.getByLabelText('Candidate monthly base fee'), '14.90')
    await user.type(screen.getByLabelText('Candidate price per kWh'), '0.3150')
    await user.type(screen.getByLabelText('Switching bonus (optional)'), '-50')

    expect(screen.getByRole('button', { name: 'Compare' })).toBeDisabled()
  })

  it('a validation error from the server is shown', async () => {
    vi.stubGlobal('fetch', vi.fn(() => Promise.resolve(new Response(JSON.stringify({ detail: 'Invalid candidate' }), { status: 400 }))))
    const user = userEvent.setup()

    render(<TariffComparisonForm currency="EUR" locale="en-US" />)

    await user.type(screen.getByLabelText('Candidate monthly base fee'), '14.90')
    await user.type(screen.getByLabelText('Candidate price per kWh'), '0.3150')
    await user.click(screen.getByRole('button', { name: 'Compare' }))

    expect(await screen.findByText('Invalid candidate')).toBeInTheDocument()
  })
})
