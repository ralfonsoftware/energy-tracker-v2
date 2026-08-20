import { render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { SmartPlugImportPanel } from './smart-plug-import-panel'

function jsonResponse(body: object | null, status = 200) {
  return new Response(body === null ? null : JSON.stringify(body), { status })
}

function makeFile(name: string) {
  return new File(['data'], name)
}

async function selectFile(file: File) {
  const input = document.querySelector('input[type="file"]') as HTMLInputElement
  await userEvent.upload(input, file)
}

describe('SmartPlugImportPanel', () => {
  beforeEach(() => {
    vi.useFakeTimers({ shouldAdvanceTime: true })
  })

  afterEach(() => {
    vi.unstubAllGlobals()
    vi.useRealTimers()
  })

  it('renders the dropzone with a hidden file input by default', () => {
    render(<SmartPlugImportPanel />)

    expect(screen.getByText('Drop a file here, or choose one to upload.')).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Choose file' })).toBeInTheDocument()
  })

  it('uploads the selected file immediately and shows Processing while polling', async () => {
    const fetchMock = vi.fn(() => Promise.resolve(jsonResponse({ jobId: 'job-1' }, 202)))
    vi.stubGlobal('fetch', fetchMock)
    render(<SmartPlugImportPanel />)

    await selectFile(makeFile('export.xlsx'))

    await waitFor(() => expect(screen.getByText('Processing')).toBeInTheDocument())
    expect(fetchMock).toHaveBeenCalledWith('/api/smart-plug-imports', expect.objectContaining({ method: 'POST' }))
    expect(screen.getByText('export.xlsx')).toBeInTheDocument()
  })

  it('polls GET /api/jobs/{id} until the job completes, then shows the complete state', async () => {
    const fetchMock = vi.fn((url: string) => {
      if (url === '/api/smart-plug-imports') {
        return Promise.resolve(jsonResponse({ jobId: 'job-1' }, 202))
      }

      return Promise.resolve(
        jsonResponse({ id: 'job-1', status: 'completed', importStatus: 'completed', errorMessage: null, createdAtUtc: '', completedAtUtc: '' }),
      )
    })
    vi.stubGlobal('fetch', fetchMock)
    render(<SmartPlugImportPanel />)

    await selectFile(makeFile('export.xlsx'))
    await waitFor(() => expect(screen.getByText('Processing')).toBeInTheDocument())

    await vi.advanceTimersByTimeAsync(2000)

    await waitFor(() => expect(screen.getByText('Import complete')).toBeInTheDocument())
  })

  it('shows the failed state when the job reaches a failed status', async () => {
    const fetchMock = vi.fn((url: string) => {
      if (url === '/api/smart-plug-imports') {
        return Promise.resolve(jsonResponse({ jobId: 'job-1' }, 202))
      }

      return Promise.resolve(
        jsonResponse({ id: 'job-1', status: 'failed', errorMessage: 'boom', createdAtUtc: '', completedAtUtc: '' }),
      )
    })
    vi.stubGlobal('fetch', fetchMock)
    render(<SmartPlugImportPanel />)

    await selectFile(makeFile('export.xlsx'))
    await waitFor(() => expect(screen.getByText('Processing')).toBeInTheDocument())

    await vi.advanceTimersByTimeAsync(2000)

    await waitFor(() => expect(screen.getByText('Import failed')).toBeInTheDocument())
    expect(screen.getByText('boom')).toBeInTheDocument()
  })

  it('shows a needs-mapping badge when the import completes without a Power Point match', async () => {
    const fetchMock = vi.fn((url: string) => {
      if (url === '/api/smart-plug-imports') {
        return Promise.resolve(jsonResponse({ jobId: 'job-1' }, 202))
      }

      return Promise.resolve(
        jsonResponse({
          id: 'job-1',
          status: 'completed',
          importStatus: 'awaitingpowerpointmapping',
          errorMessage: null,
          createdAtUtc: '',
          completedAtUtc: '',
        }),
      )
    })
    vi.stubGlobal('fetch', fetchMock)
    render(<SmartPlugImportPanel />)

    await selectFile(makeFile('export.xlsx'))
    await waitFor(() => expect(screen.getByText('Processing')).toBeInTheDocument())

    await vi.advanceTimersByTimeAsync(2000)

    await waitFor(() => expect(screen.getByText('Needs Power Point mapping')).toBeInTheDocument())
  })

  it('tolerates a transient polling failure and keeps polling instead of failing immediately', async () => {
    let statusCallCount = 0
    const fetchMock = vi.fn((url: string) => {
      if (url === '/api/smart-plug-imports') {
        return Promise.resolve(jsonResponse({ jobId: 'job-1' }, 202))
      }

      statusCallCount += 1
      if (statusCallCount === 1) {
        return Promise.reject(new Error('network blip'))
      }

      return Promise.resolve(
        jsonResponse({ id: 'job-1', status: 'completed', importStatus: 'completed', errorMessage: null, createdAtUtc: '', completedAtUtc: '' }),
      )
    })
    vi.stubGlobal('fetch', fetchMock)
    render(<SmartPlugImportPanel />)

    await selectFile(makeFile('export.xlsx'))
    await waitFor(() => expect(screen.getByText('Processing')).toBeInTheDocument())

    await vi.advanceTimersByTimeAsync(2000)
    // Single transient failure — must still be polling, not failed.
    expect(screen.queryByText('Import failed')).not.toBeInTheDocument()

    await vi.advanceTimersByTimeAsync(2000)
    await waitFor(() => expect(screen.getByText('Import complete')).toBeInTheDocument())
  })

  it('shows an error and returns to idle when the upload itself is rejected (unsupported type)', async () => {
    vi.stubGlobal('fetch', vi.fn(() => Promise.resolve(jsonResponse({ detail: 'bad type' }, 400))))
    render(<SmartPlugImportPanel />)

    // The input's accept=".xlsx,.csv" only narrows the OS file picker — the server is still the
    // authority on what's accepted, so a matching-extension file can still come back 400 (e.g. a
    // corrupt/empty upload).
    await selectFile(makeFile('notes.csv'))

    await waitFor(() =>
      expect(screen.getByText("That file type isn't supported. Please choose an .xlsx or .csv export.")).toBeInTheDocument(),
    )
    expect(screen.getByRole('button', { name: 'Choose file' })).toBeInTheDocument()
  })

  it('clears the polling interval on unmount', async () => {
    const fetchMock = vi.fn((url: string) => {
      if (url === '/api/smart-plug-imports') {
        return Promise.resolve(jsonResponse({ jobId: 'job-1' }, 202))
      }

      return Promise.resolve(
        jsonResponse({ id: 'job-1', status: 'processing', errorMessage: null, createdAtUtc: '', completedAtUtc: null }),
      )
    })
    vi.stubGlobal('fetch', fetchMock)
    const { unmount } = render(<SmartPlugImportPanel />)

    await selectFile(makeFile('export.xlsx'))
    await waitFor(() => expect(screen.getByText('Processing')).toBeInTheDocument())

    const callCountAtUnmount = fetchMock.mock.calls.length
    unmount()

    await vi.advanceTimersByTimeAsync(10000)

    expect(fetchMock.mock.calls.length).toBe(callCountAtUnmount)
  })
})
