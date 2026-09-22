import { render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { DataExportPanel } from './data-export-panel'

const url = '/api/household-export'

describe('DataExportPanel', () => {
  afterEach(() => {
    vi.unstubAllGlobals()
  })

  it('triggers a download with the server-provided filename on success', async () => {
    const body = JSON.stringify({ formatVersion: 'v2' })
    const response = new Response(body, {
      status: 200,
      headers: {
        'Content-Type': 'application/json',
        'Content-Disposition': 'attachment; filename="energy-tracker-export-2026-09-22.json"',
      },
    })
    const fetchMock = vi.fn((input: string | URL | Request) => {
      expect(String(input)).toBe(url)
      return Promise.resolve(response)
    })
    vi.stubGlobal('fetch', fetchMock)
    const createObjectURL = vi.fn(() => 'blob:mock-url')
    const revokeObjectURL = vi.fn()
    vi.stubGlobal('URL', { ...URL, createObjectURL, revokeObjectURL })
    const clickSpy = vi.spyOn(HTMLAnchorElement.prototype, 'click').mockImplementation(() => {})

    render(<DataExportPanel />)
    const user = userEvent.setup()
    await user.click(screen.getByRole('button', { name: /export data/i }))

    expect(await screen.findByRole('button', { name: /export data/i })).toBeInTheDocument()
    expect(fetchMock).toHaveBeenCalledTimes(1)
    expect(createObjectURL).toHaveBeenCalledTimes(1)
    expect(clickSpy).toHaveBeenCalledTimes(1)
    expect(revokeObjectURL).toHaveBeenCalledWith('blob:mock-url')

    clickSpy.mockRestore()
  })

  it('shows an error message when the export request fails', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn(() => Promise.resolve(new Response(JSON.stringify({ detail: 'boom' }), { status: 500 }))),
    )

    render(<DataExportPanel />)
    const user = userEvent.setup()
    await user.click(screen.getByRole('button', { name: /export data/i }))

    expect(await screen.findByText('boom')).toBeInTheDocument()
  })

  it('shows a generic error message when the failure response has no detail', async () => {
    vi.stubGlobal('fetch', vi.fn(() => Promise.resolve(new Response(null, { status: 403 }))))

    render(<DataExportPanel />)
    const user = userEvent.setup()
    await user.click(screen.getByRole('button', { name: /export data/i }))

    expect(await screen.findByText(/something went wrong/i)).toBeInTheDocument()
  })
})
