import { render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { MeterRegressionPromptDialog } from './meter-regression-prompt-dialog'
import type { MeterRegressionPromptDto } from '@/lib/meter-regression-api'

function jsonResponse(body: object | null, status = 200) {
  return new Response(body === null ? null : JSON.stringify(body), { status })
}

const basePrompt: MeterRegressionPromptDto = {
  id: 'prompt-1',
  meterReadingId: 'reading-1',
  readingKwhValue: 412,
  readingTimestamp: '2026-08-16T19:42:00+00:00',
  previousMeterReadingId: 'reading-0',
  previousReadingKwhValue: 14302,
  previousReadingTimestamp: '2026-08-15T19:42:00+00:00',
  mainMeterDigitCapacityKwh: null,
}

describe('MeterRegressionPromptDialog', () => {
  afterEach(() => {
    vi.unstubAllGlobals()
  })

  it('renders nothing open when prompt is null', () => {
    render(<MeterRegressionPromptDialog prompt={null} onResolved={vi.fn()} />)

    expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
  })

  it('renders the two readings\' values from the supplied prompt, announced via role and accessible name', async () => {
    render(<MeterRegressionPromptDialog prompt={basePrompt} onResolved={vi.fn()} />)

    const dialog = await screen.findByRole('dialog', { name: 'That reading is lower than the last one' })
    expect(dialog).toBeInTheDocument()
    expect(screen.getByText(/412/)).toBeInTheDocument()
    expect(screen.getByText(/14302/)).toBeInTheDocument()
  })

  it('"Meter was reset/replaced" resolves immediately with no extra input', async () => {
    const fetchMock = vi.fn(() => Promise.resolve(jsonResponse({ id: 'prompt-1', classification: 'reset', resolvedAtUtc: '2026-08-16T19:45:00+00:00' })))
    vi.stubGlobal('fetch', fetchMock)
    const onResolved = vi.fn()
    const user = userEvent.setup()
    render(<MeterRegressionPromptDialog prompt={basePrompt} onResolved={onResolved} />)

    await user.click(await screen.findByRole('button', { name: /The meter was reset/ }))

    await waitFor(() => expect(onResolved).toHaveBeenCalledTimes(1))
    expect(fetchMock).toHaveBeenCalledWith(
      '/api/meter-regression-prompts/prompt-1/resolve',
      expect.objectContaining({
        method: 'POST',
        body: JSON.stringify({ classification: 'reset', digitCapacityKwh: null }),
      }),
    )
  })

  it('"Meter rolled over" reveals the capacity field, required and empty when no capacity is known', async () => {
    const user = userEvent.setup()
    render(<MeterRegressionPromptDialog prompt={basePrompt} onResolved={vi.fn()} />)

    await user.click(await screen.findByRole('button', { name: /It rolled over/ }))

    const capacityField = await screen.findByLabelText("Meter's digit capacity (kWh)")
    expect(capacityField).toHaveValue(null)
    expect(capacityField).toBeRequired()
    expect(screen.getByRole('button', { name: 'Confirm' })).toBeDisabled()
  })

  it('"Meter rolled over" pre-fills the capacity field when MainMeterDigitCapacityKwh is known', async () => {
    const user = userEvent.setup()
    render(<MeterRegressionPromptDialog prompt={{ ...basePrompt, mainMeterDigitCapacityKwh: 99999 }} onResolved={vi.fn()} />)

    await user.click(await screen.findByRole('button', { name: /It rolled over/ }))

    expect(await screen.findByLabelText("Meter's digit capacity (kWh)")).toHaveValue(99999)
    expect(screen.getByRole('button', { name: 'Confirm' })).toBeEnabled()
  })

  it('confirming a rollover posts the entered digit capacity', async () => {
    const fetchMock = vi.fn(() => Promise.resolve(jsonResponse({ id: 'prompt-1', classification: 'rollover', resolvedAtUtc: '2026-08-16T19:45:00+00:00' })))
    vi.stubGlobal('fetch', fetchMock)
    const onResolved = vi.fn()
    const user = userEvent.setup()
    render(<MeterRegressionPromptDialog prompt={basePrompt} onResolved={onResolved} />)

    await user.click(await screen.findByRole('button', { name: /It rolled over/ }))
    await user.type(await screen.findByLabelText("Meter's digit capacity (kWh)"), '99999')
    await user.click(screen.getByRole('button', { name: 'Confirm' }))

    await waitFor(() => expect(onResolved).toHaveBeenCalledTimes(1))
    expect(fetchMock).toHaveBeenCalledWith(
      '/api/meter-regression-prompts/prompt-1/resolve',
      expect.objectContaining({
        method: 'POST',
        body: JSON.stringify({ classification: 'rollover', digitCapacityKwh: 99999 }),
      }),
    )
  })
})
