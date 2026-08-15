import 'fake-indexeddb/auto'
import { IDBFactory } from 'fake-indexeddb'
import { render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { LogReadingSheet } from './log-reading-sheet'
import { listPending } from '@/lib/offline-queue'

function jsonResponse(body: object | null, status = 200) {
  return new Response(body === null ? null : JSON.stringify(body), { status })
}

beforeEach(() => {
  globalThis.indexedDB = new IDBFactory()
})

describe('LogReadingSheet', () => {
  afterEach(() => {
    vi.unstubAllGlobals()
  })

  it('opens with today\'s date/time pre-filled and editable', async () => {
    const user = userEvent.setup()
    render(<LogReadingSheet trigger={<button>Log reading</button>} />)

    await user.click(screen.getByRole('button', { name: 'Log reading' }))

    const dateTimeInput = await screen.findByLabelText('Date & time')
    const now = new Date()
    const expectedPrefix = `${now.getFullYear()}-${String(now.getMonth() + 1).padStart(2, '0')}-${String(now.getDate()).padStart(2, '0')}T`
    expect((dateTimeInput as HTMLInputElement).value.startsWith(expectedPrefix)).toBe(true)

    await user.clear(dateTimeInput)
    await user.type(dateTimeInput, '2026-08-01T09:15')
    expect(dateTimeInput).toHaveValue('2026-08-01T09:15')
  })

  it('a successful save closes the sheet', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn(() => Promise.resolve(jsonResponse({ id: 'r1', kwhValue: 4821.5, readingTimestamp: '2026-08-15T14:32:00+00:00' }))),
    )
    const user = userEvent.setup()
    render(<LogReadingSheet trigger={<button>Log reading</button>} />)

    await user.click(screen.getByRole('button', { name: 'Log reading' }))
    await user.type(await screen.findByLabelText('kWh'), '4821.5')
    await user.click(screen.getByRole('button', { name: 'Save reading' }))

    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument())
    expect(await screen.findByText(/Saved: 4821.5 kWh/)).toBeInTheDocument()
  })

  it('a simulated network failure enqueues to IndexedDB and shows the offline-queued confirmation instead of an error', async () => {
    vi.stubGlobal('fetch', vi.fn(() => Promise.reject(new TypeError('Failed to fetch'))))
    const user = userEvent.setup()
    render(<LogReadingSheet trigger={<button>Log reading</button>} />)

    await user.click(screen.getByRole('button', { name: 'Log reading' }))
    await user.type(await screen.findByLabelText('kWh'), '4821.5')
    await user.click(screen.getByRole('button', { name: 'Save reading' }))

    expect(await screen.findByText("Saved — will sync when you're back online")).toBeInTheDocument()
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument()

    const pending = await listPending()
    expect(pending).toHaveLength(1)
    expect(pending[0].kwhValue).toBe(4821.5)
  })

  it('Save and the inputs are disabled while a request is in flight', async () => {
    let resolveFetch!: (response: Response) => void
    vi.stubGlobal(
      'fetch',
      vi.fn(
        () =>
          new Promise<Response>((resolve) => {
            resolveFetch = resolve
          }),
      ),
    )
    const user = userEvent.setup()
    render(<LogReadingSheet trigger={<button>Log reading</button>} />)

    await user.click(screen.getByRole('button', { name: 'Log reading' }))
    await user.type(await screen.findByLabelText('kWh'), '4821.5')
    await user.click(screen.getByRole('button', { name: 'Save reading' }))

    expect(await screen.findByRole('button', { name: 'Saving…' })).toBeDisabled()
    expect(screen.getByLabelText('kWh')).toBeDisabled()
    expect(screen.getByLabelText('Date & time')).toBeDisabled()

    resolveFetch(jsonResponse({ id: 'r1', kwhValue: 4821.5, readingTimestamp: '2026-08-15T14:32:00+00:00' }))
    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument())
  })
})
