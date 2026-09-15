import { render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { TariffComparisonForm } from './tariff-comparison-form'

function jsonResponse(body: object | null, status = 200) {
  return new Response(body === null ? null : JSON.stringify(body), { status })
}

// mockups/key-tariff-radar.html's own worked example (Story 5.2 Dev Notes, Story 5.3 Dev Notes):
// current €12.50/mo + €0.3200/kWh, candidate €14.90/mo + €0.3150/kWh + €350 switching bonus,
// pace 3,200 kWh/yr -> bonus-included: "Save about €337.20 this year" (worth it); bonus-normalized:
// "About €12.80/yr more" (not worth it) — the same candidate reads as a win with the bonus and a
// loss without it, dramatizing FR-13's whole point.
const mockupWorkedExample = {
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
  candidateAnnualCostBonusIncluded: 836.8,
  bonusIncludedAnnualSavings: 337.2,
  isBonusIncludedWorthSwitching: true,
  isBonusNormalizedWorthSwitching: false,
  isLowConfidence: false,
}

describe('TariffComparisonForm', () => {
  afterEach(() => {
    vi.unstubAllGlobals()
  })

  it('renders both signal rows together, always, never toggled (AC #1)', async () => {
    vi.stubGlobal('fetch', vi.fn(() => Promise.resolve(jsonResponse(mockupWorkedExample))))
    const user = userEvent.setup()

    render(<TariffComparisonForm currency="EUR" locale="en-US" />)

    await user.type(screen.getByLabelText('Candidate monthly base fee'), '14.90')
    await user.type(screen.getByLabelText('Candidate price per kWh'), '0.3150')
    await user.type(screen.getByLabelText('Switching bonus (optional)'), '350')
    await user.click(screen.getByRole('button', { name: 'Compare' }))

    // Both rows present simultaneously — proving neither is toggled/hidden behind the other.
    expect(await screen.findByText('Worth switching')).toBeInTheDocument()
    expect(screen.getByText('Not worth it')).toBeInTheDocument()
    expect(screen.getByText('Save about 337.20 EUR this year')).toBeInTheDocument()
    expect(screen.getByText('About 12.80 EUR/yr more once the bonus is gone')).toBeInTheDocument()
    expect(screen.getByText('Based on your actual pace of 3,200 kWh/yr.')).toBeInTheDocument()
  })

  it('a breakeven (zero) savings row renders "Not worth it", per row, independently of the other row (AC #2)', async () => {
    const comparison = {
      ...mockupWorkedExample,
      bonusNormalizedAnnualSavings: 0,
      isBonusNormalizedWorthSwitching: false,
    }
    vi.stubGlobal('fetch', vi.fn(() => Promise.resolve(jsonResponse(comparison))))
    const user = userEvent.setup()

    render(<TariffComparisonForm currency="EUR" locale="en-US" />)
    await user.type(screen.getByLabelText('Candidate monthly base fee'), '14.90')
    await user.type(screen.getByLabelText('Candidate price per kWh'), '0.3150')
    await user.click(screen.getByRole('button', { name: 'Compare' }))

    // Bonus-included row still reads worth-it (unaffected); bonus-normalized reads not-worth-it
    // at exactly zero — the tie-break rule resolved to "not worth it", not toggled by the sign check.
    expect(await screen.findByText('Worth switching')).toBeInTheDocument()
    expect(screen.getByText('Not worth it')).toBeInTheDocument()
    expect(screen.getByText('About 0.00 EUR/yr more once the bonus is gone')).toBeInTheDocument()
  })

  it('both rows render "Worth switching" when the candidate wins even after the bonus fully decays', async () => {
    // Dev Notes: all three verdict combinations (mixed, both green, both red) must render
    // correctly, not just the mockup's own mixed worked example.
    const comparison = {
      ...mockupWorkedExample,
      candidateAnnualCostBonusNormalized: 720,
      bonusNormalizedAnnualSavings: 720,
      candidateAnnualCostBonusIncluded: 620,
      bonusIncludedAnnualSavings: 820,
      isBonusIncludedWorthSwitching: true,
      isBonusNormalizedWorthSwitching: true,
    }
    vi.stubGlobal('fetch', vi.fn(() => Promise.resolve(jsonResponse(comparison))))
    const user = userEvent.setup()

    render(<TariffComparisonForm currency="EUR" locale="en-US" />)
    await user.type(screen.getByLabelText('Candidate monthly base fee'), '14.90')
    await user.type(screen.getByLabelText('Candidate price per kWh'), '0.3150')
    await user.click(screen.getByRole('button', { name: 'Compare' }))

    expect(await screen.findAllByText('Worth switching')).toHaveLength(2)
    expect(screen.queryByText('Not worth it')).not.toBeInTheDocument()
  })

  it('the bonus-normalized row states the candidate\'s ongoing rate at the household\'s actual pace', async () => {
    vi.stubGlobal('fetch', vi.fn(() => Promise.resolve(jsonResponse(mockupWorkedExample))))
    const user = userEvent.setup()

    render(<TariffComparisonForm currency="EUR" locale="en-US" />)
    await user.type(screen.getByLabelText('Candidate monthly base fee'), '14.90')
    await user.type(screen.getByLabelText('Candidate price per kWh'), '0.3150')
    await user.type(screen.getByLabelText('Switching bonus (optional)'), '350')
    await user.click(screen.getByRole('button', { name: 'Compare' }))

    expect(await screen.findByText("Based on the candidate's ongoing rate at your actual pace of 3,200 kWh/yr.")).toBeInTheDocument()
  })

  it('the bonus-included row does not claim "even with the bonus" when no bonus was entered', async () => {
    const comparison = {
      ...mockupWorkedExample,
      candidateSwitchingBonus: 0,
      candidateAnnualCostBonusIncluded: 1186.8,
      bonusIncludedAnnualSavings: -12.8,
      isBonusIncludedWorthSwitching: false,
    }
    vi.stubGlobal('fetch', vi.fn(() => Promise.resolve(jsonResponse(comparison))))
    const user = userEvent.setup()

    render(<TariffComparisonForm currency="EUR" locale="en-US" />)
    await user.type(screen.getByLabelText('Candidate monthly base fee'), '14.90')
    await user.type(screen.getByLabelText('Candidate price per kWh'), '0.3150')
    await user.click(screen.getByRole('button', { name: 'Compare' }))

    expect(await screen.findByText('About 12.80 EUR more this year')).toBeInTheDocument()
    expect(screen.queryByText(/even with the bonus/)).not.toBeInTheDocument()
  })

  it('the current-vs-candidate summary panels render the right label/value pairs with 2-decimal money formatting (AC #4)', async () => {
    vi.stubGlobal('fetch', vi.fn(() => Promise.resolve(jsonResponse(mockupWorkedExample))))
    const user = userEvent.setup()

    render(<TariffComparisonForm currency="EUR" locale="en-US" />)
    await user.type(screen.getByLabelText('Candidate monthly base fee'), '14.90')
    await user.type(screen.getByLabelText('Candidate price per kWh'), '0.3150')
    await user.type(screen.getByLabelText('Switching bonus (optional)'), '350')
    await user.click(screen.getByRole('button', { name: 'Compare' }))

    expect(await screen.findByText('Your current tariff')).toBeInTheDocument()
    expect(screen.getByText('Candidate tariff')).toBeInTheDocument()
    expect(screen.getByText('12.50 EUR')).toBeInTheDocument()
    expect(screen.getByText('14.90 EUR')).toBeInTheDocument()
    // Current (0.3200) and candidate (0.3150) price/kWh both round to the same 2-decimal display
    // at this precision — the summary panels deliberately reuse moneyFormat (2 decimals), not the
    // 4-decimal priceFormat used elsewhere (Task 4's own instruction), so both rows read "0.32".
    expect(screen.getAllByText('0.32 EUR/kWh')).toHaveLength(2)
    expect(screen.getByText('350.00 EUR')).toBeInTheDocument()
  })

  it('the switching-bonus summary row is omitted when the bonus is 0 (AC #4)', async () => {
    const comparison = {
      ...mockupWorkedExample,
      candidateSwitchingBonus: 0,
      candidateAnnualCostBonusIncluded: 1186.8,
      bonusIncludedAnnualSavings: -12.8,
      isBonusIncludedWorthSwitching: false,
    }
    vi.stubGlobal('fetch', vi.fn(() => Promise.resolve(jsonResponse(comparison))))
    const user = userEvent.setup()

    render(<TariffComparisonForm currency="EUR" locale="en-US" />)
    await user.type(screen.getByLabelText('Candidate monthly base fee'), '14.90')
    await user.type(screen.getByLabelText('Candidate price per kWh'), '0.3150')
    await user.click(screen.getByRole('button', { name: 'Compare' }))

    await screen.findByText('Candidate tariff')
    // The label appears once — as the form field's own <Label>, not duplicated as a summary row.
    expect(screen.getAllByText('Switching bonus (optional)')).toHaveLength(1)
    // No bonus-included detail sentence either — nothing to explain when nothing was entered.
    expect(screen.queryByText(/switching bonus, credited in year one/)).not.toBeInTheDocument()
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
      ...mockupWorkedExample,
      candidateSwitchingBonus: 0,
      candidateAnnualCostBonusIncluded: 1186.8,
      bonusIncludedAnnualSavings: -12.8,
      isBonusIncludedWorthSwitching: false,
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
    expect(await screen.findByText('About 12.80 EUR/yr more once the bonus is gone')).toBeInTheDocument()

    await user.click(screen.getByRole('button', { name: 'Compare' }))

    expect(await screen.findByText('Invalid candidate')).toBeInTheDocument()
    expect(screen.queryByText('About 12.80 EUR/yr more once the bonus is gone')).not.toBeInTheDocument()
  })

  it('a low-confidence result shows the stale-pace footnote once, not duplicated per row', async () => {
    const comparison = { ...mockupWorkedExample, isLowConfidence: true }
    vi.stubGlobal('fetch', vi.fn(() => Promise.resolve(jsonResponse(comparison))))
    const user = userEvent.setup()

    render(<TariffComparisonForm currency="EUR" locale="en-US" />)
    await user.type(screen.getByLabelText('Candidate monthly base fee'), '14.90')
    await user.type(screen.getByLabelText('Candidate price per kWh'), '0.3150')
    await user.click(screen.getByRole('button', { name: 'Compare' }))

    expect(await screen.findByText("It's been a while since your last reading, so this pace may be less reliable than usual.")).toBeInTheDocument()
    expect(
      screen.getAllByText("It's been a while since your last reading, so this pace may be less reliable than usual.")
    ).toHaveLength(1)
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
