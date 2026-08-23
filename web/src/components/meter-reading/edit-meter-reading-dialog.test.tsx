import { render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { EditMeterReadingDialog } from './edit-meter-reading-dialog'
import type { MeterReadingHistoryItemDto } from '@/lib/meter-reading-history-api'

function jsonResponse(body: object | null, status = 200) {
  return new Response(body === null ? null : JSON.stringify(body), { status })
}

function reading(overrides: Partial<MeterReadingHistoryItemDto> = {}): MeterReadingHistoryItemDto {
  return {
    id: '11111111-1111-1111-1111-111111111111',
    kwhValue: 4821.5,
    readingTimestamp: '2026-08-15T14:32:00+00:00',
    version: 3,
    isPendingRegression: false,
    correctedFromKwhValue: null,
    correctedAtUtc: null,
    ...overrides,
  }
}

describe('EditMeterReadingDialog', () => {
  afterEach(() => {
    vi.unstubAllGlobals()
  })

  it('submitting a new value calls updateMeterReading with the current version, and success calls onSaved and closes', async () => {
    const fetchMock = vi.fn(() =>
      Promise.resolve(jsonResponse({ id: reading().id, kwhValue: 5000, readingTimestamp: reading().readingTimestamp, version: 4 })),
    )
    vi.stubGlobal('fetch', fetchMock)
    const user = userEvent.setup()
    const onSaved = vi.fn()
    const onOpenChange = vi.fn()

    render(<EditMeterReadingDialog reading={reading()} open={true} onOpenChange={onOpenChange} onSaved={onSaved} />)

    const input = screen.getByLabelText('kWh')
    await user.clear(input)
    await user.type(input, '5000')
    await user.click(screen.getByRole('button', { name: 'Save' }))

    expect(fetchMock).toHaveBeenCalledWith(
      `/api/meter-readings/${reading().id}`,
      expect.objectContaining({
        method: 'PUT',
        body: JSON.stringify({ kwhValue: 5000, version: 3 }),
      }),
    )
    await vi.waitFor(() => expect(onSaved).toHaveBeenCalledOnce())
    expect(onOpenChange).toHaveBeenCalledWith(false)
  })

  it('a 409 conflict shows the conflict message and does not call onSaved', async () => {
    vi.stubGlobal('fetch', vi.fn(() => Promise.resolve(new Response(JSON.stringify({ detail: 'stale' }), { status: 409 }))))
    const user = userEvent.setup()
    const onSaved = vi.fn()

    render(<EditMeterReadingDialog reading={reading()} open={true} onOpenChange={() => {}} onSaved={onSaved} />)

    await user.click(screen.getByRole('button', { name: 'Save' }))

    expect(await screen.findByText('This reading was changed elsewhere — refresh and try again.')).toBeInTheDocument()
    expect(onSaved).not.toHaveBeenCalled()
  })

  it('a non-409 error shows the generic error message', async () => {
    vi.stubGlobal('fetch', vi.fn(() => Promise.resolve(new Response(null, { status: 500 }))))
    const user = userEvent.setup()
    const onSaved = vi.fn()

    render(<EditMeterReadingDialog reading={reading()} open={true} onOpenChange={() => {}} onSaved={onSaved} />)

    await user.click(screen.getByRole('button', { name: 'Save' }))

    expect(await screen.findByText('Something went wrong. Please try again.')).toBeInTheDocument()
    expect(onSaved).not.toHaveBeenCalled()
  })
})
