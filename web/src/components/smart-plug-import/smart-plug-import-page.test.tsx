import { StrictMode } from 'react'
import { fireEvent, render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { SmartPlugImportPage } from './smart-plug-import-page'

function jsonResponse(body: object | null, status = 200) {
  return new Response(body === null ? null : JSON.stringify(body), { status })
}

// SmartPlugImportPage now also renders JobHistoryList (Story 3.6), which fetches
// GET /api/smart-plug-import-jobs on mount and on its own poll interval — every fetch mock below
// needs to answer that call with an array (its own contract), not fall through to whatever
// job-status shape that mock's own handler returns for "any other URL". Wraps a test's own
// per-URL handler so it doesn't have to repeat this branch itself.
function withJobHistoryStub(
  handler: (url: string, init?: RequestInit) => Promise<Response>,
): (input: string | URL | Request, init?: RequestInit) => Promise<Response> {
  return (input, init) => {
    const url = String(input)
    if (url === '/api/smart-plug-import-jobs') {
      return Promise.resolve(jsonResponse([]))
    }
    return handler(url, init)
  }
}

function makeFile(name: string) {
  return new File(['data'], name)
}

async function selectFiles(...files: File[]) {
  const input = document.querySelector('input[type="file"]') as HTMLInputElement
  await userEvent.upload(input, files)
}

function dropFiles(...files: File[]) {
  const dropzone = screen.getByTestId('smart-plug-import-dropzone')
  fireEvent.drop(dropzone, { dataTransfer: { files } })
}

function fileNameFromUploadInit(init?: RequestInit): string {
  const formData = init?.body as FormData
  return (formData.get('file') as File).name
}

describe('SmartPlugImportPage', () => {
  beforeEach(() => {
    vi.useFakeTimers({ shouldAdvanceTime: true })
  })

  afterEach(() => {
    vi.unstubAllGlobals()
    vi.useRealTimers()
  })

  it('renders the dropzone with a hidden, multi-file input by default', () => {
    render(<SmartPlugImportPage onBack={() => {}} />)

    expect(screen.getByText('Drop a file here, or choose one to upload.')).toBeInTheDocument()
    const input = document.querySelector('input[type="file"]') as HTMLInputElement
    expect(input).toHaveAttribute('multiple')
  })

  it('moves focus to the page heading on mount, for keyboard/screen-reader users arriving via the entry point', () => {
    render(<SmartPlugImportPage onBack={() => {}} />)

    expect(screen.getByRole('heading', { name: 'Smart Plug Import' })).toHaveFocus()
  })

  it('calls onBack when the back button is tapped', async () => {
    const onBack = vi.fn()
    render(<SmartPlugImportPage onBack={onBack} />)

    await userEvent.click(screen.getByRole('button', { name: 'Go to Energy Tracker' }))
    expect(onBack).toHaveBeenCalledOnce()
  })

  it('selecting 3 files in one action renders 3 queue rows immediately, before any upload promise resolves', async () => {
    let releaseUploads: (() => void) | null = null
    const gate = new Promise<void>((resolve) => {
      releaseUploads = resolve
    })
    // Each file gets its own distinct jobId (not a shared one) and its own /api/jobs/{id} mock,
    // so a poll tick landing mid-test — however unlikely under fake timers — exercises the real
    // per-item polling path instead of falling through to an unhandled/null response.
    const jobIdByFileName: Record<string, string> = { 'a.xlsx': 'job-a', 'b.csv': 'job-b', 'c.csv': 'job-c' }
    const fetchMock = vi.fn(withJobHistoryStub(async (url, init) => {
      if (url === '/api/smart-plug-imports') {
        await gate
        return jsonResponse({ jobId: jobIdByFileName[fileNameFromUploadInit(init)] }, 202)
      }
      if (url === '/api/jobs/job-a' || url === '/api/jobs/job-b' || url === '/api/jobs/job-c') {
        return jsonResponse({ id: url.split('/').pop(), status: 'processing', errorMessage: null, createdAtUtc: '', completedAtUtc: null })
      }
      throw new Error(`Unexpected fetch: ${url}`)
    }))
    vi.stubGlobal('fetch', fetchMock)

    render(<SmartPlugImportPage onBack={() => {}} />)
    await selectFiles(makeFile('a.xlsx'), makeFile('b.csv'), makeFile('c.csv'))

    // All 3 rows render immediately, still in the "uploading" state — the upload promise above
    // is deliberately still pending (releaseUploads not yet called).
    expect(screen.getByText('a.xlsx')).toBeInTheDocument()
    expect(screen.getByText('b.csv')).toBeInTheDocument()
    expect(screen.getByText('c.csv')).toBeInTheDocument()
    expect(screen.getAllByText('Uploading…')).toHaveLength(3)

    // Confirm uploads fired concurrently, not one-by-one: all 3 POSTs already happened even
    // though none has resolved yet. +1 for JobHistoryList's own mount-time
    // GET /api/smart-plug-import-jobs fetch (Story 3.6).
    expect(fetchMock).toHaveBeenCalledTimes(4)

    releaseUploads!()
    await waitFor(() => expect(screen.getAllByText('Processing')).toHaveLength(3))
  })

  it('dropping 3 files in one action renders 3 queue rows and uploads all 3 concurrently (AC #4/#6)', async () => {
    const fetchMock = vi.fn(withJobHistoryStub((url, init) => {
      if (url === '/api/smart-plug-imports') {
        const jobId = `job-${fileNameFromUploadInit(init)}`
        return Promise.resolve(jsonResponse({ jobId }, 202))
      }
      return Promise.resolve(jsonResponse({ id: url, status: 'processing', errorMessage: null, createdAtUtc: '', completedAtUtc: null }))
    }))
    vi.stubGlobal('fetch', fetchMock)

    render(<SmartPlugImportPage onBack={() => {}} />)
    dropFiles(makeFile('d1.xlsx'), makeFile('d2.csv'), makeFile('d3.csv'))

    expect(screen.getByText('d1.xlsx')).toBeInTheDocument()
    expect(screen.getByText('d2.csv')).toBeInTheDocument()
    expect(screen.getByText('d3.csv')).toBeInTheDocument()
    await waitFor(() => expect(screen.getAllByText('Processing')).toHaveLength(3))
    // +1 for JobHistoryList's own mount-time GET /api/smart-plug-import-jobs fetch (Story 3.6).
    expect(fetchMock).toHaveBeenCalledTimes(4)
  })

  it("one item's failure does not affect the polling or rendered state of the other two (AC #5)", async () => {
    let uploadCallCount = 0
    const fetchMock = vi.fn(withJobHistoryStub((url, init) => {
      if (url === '/api/smart-plug-imports') {
        uploadCallCount += 1
        const name = fileNameFromUploadInit(init)
        const jobId = name === 'good-1.csv' ? 'job-1' : name === 'bad.csv' ? 'job-2' : 'job-3'
        return Promise.resolve(jsonResponse({ jobId }, 202))
      }
      if (url === '/api/jobs/job-1' || url === '/api/jobs/job-3') {
        return Promise.resolve(
          jsonResponse({ id: 'job', status: 'completed', importStatus: 'completed', errorMessage: null, createdAtUtc: '', completedAtUtc: '' }),
        )
      }
      if (url === '/api/jobs/job-2') {
        return Promise.resolve(jsonResponse({ id: 'job-2', status: 'failed', errorMessage: 'parse error', createdAtUtc: '', completedAtUtc: '' }))
      }
      throw new Error(`Unexpected fetch: ${url}`)
    }))
    vi.stubGlobal('fetch', fetchMock)

    render(<SmartPlugImportPage onBack={() => {}} />)
    await selectFiles(makeFile('good-1.csv'), makeFile('bad.csv'), makeFile('good-2.csv'))

    expect(uploadCallCount).toBe(3)
    await waitFor(() => expect(screen.getAllByText('Processing')).toHaveLength(3))

    await vi.advanceTimersByTimeAsync(2000)

    await waitFor(() => expect(screen.getAllByText('Import complete')).toHaveLength(2))
    expect(screen.getByText('Import failed')).toBeInTheDocument()
    expect(screen.getByText('parse error')).toBeInTheDocument()
  })

  it('uploads a selected file exactly once under React StrictMode dev double-invocation', async () => {
    // A real `fetch` rejects with AbortError once its request's AbortSignal fires — this mock
    // reproduces that so the test actually exercises the fix (aborting the first of StrictMode's
    // two effect invocations), rather than a bare mock that would "succeed" twice regardless of
    // whether the code aborts anything.
    let successfulUploads = 0
    const fetchMock = vi.fn(withJobHistoryStub((url, init) => {
      if (url === '/api/smart-plug-imports') {
        return new Promise<Response>((resolve, reject) => {
          const signal = init?.signal
          if (signal?.aborted) {
            reject(new DOMException('Aborted', 'AbortError'))
            return
          }
          signal?.addEventListener('abort', () => reject(new DOMException('Aborted', 'AbortError')))
          queueMicrotask(() => {
            if (signal?.aborted) {
              return
            }
            successfulUploads += 1
            resolve(jsonResponse({ jobId: 'job-1' }, 202))
          })
        })
      }
      return Promise.resolve(
        jsonResponse({ id: 'job-1', status: 'processing', errorMessage: null, createdAtUtc: '', completedAtUtc: null }),
      )
    }))
    vi.stubGlobal('fetch', fetchMock)
    render(<SmartPlugImportPage onBack={() => {}} />, { wrapper: StrictMode })

    await selectFiles(makeFile('export.xlsx'))
    await waitFor(() => expect(screen.getByText('Processing')).toBeInTheDocument())

    expect(successfulUploads).toBe(1)
  })

  it('uploads the selected file immediately and shows Processing while polling', async () => {
    const fetchMock = vi.fn(withJobHistoryStub(() => Promise.resolve(jsonResponse({ jobId: 'job-1' }, 202))))
    vi.stubGlobal('fetch', fetchMock)
    render(<SmartPlugImportPage onBack={() => {}} />)

    await selectFiles(makeFile('export.xlsx'))

    await waitFor(() => expect(screen.getByText('Processing')).toBeInTheDocument())
    expect(fetchMock).toHaveBeenCalledWith('/api/smart-plug-imports', expect.objectContaining({ method: 'POST' }))
    expect(screen.getByText('export.xlsx')).toBeInTheDocument()
  })

  it('polls GET /api/jobs/{id} until the job completes, then shows the complete state', async () => {
    const fetchMock = vi.fn(withJobHistoryStub((url) => {
      if (url === '/api/smart-plug-imports') {
        return Promise.resolve(jsonResponse({ jobId: 'job-1' }, 202))
      }

      return Promise.resolve(
        jsonResponse({ id: 'job-1', status: 'completed', importStatus: 'completed', errorMessage: null, createdAtUtc: '', completedAtUtc: '' }),
      )
    }))
    vi.stubGlobal('fetch', fetchMock)
    render(<SmartPlugImportPage onBack={() => {}} />)

    await selectFiles(makeFile('export.xlsx'))
    await waitFor(() => expect(screen.getByText('Processing')).toBeInTheDocument())

    await vi.advanceTimersByTimeAsync(2000)

    await waitFor(() => expect(screen.getByText('Import complete')).toBeInTheDocument())
  })

  it('hides the shared background-processing note once the (only) queued item has completed', async () => {
    const fetchMock = vi.fn(withJobHistoryStub((url) => {
      if (url === '/api/smart-plug-imports') {
        return Promise.resolve(jsonResponse({ jobId: 'job-1' }, 202))
      }

      return Promise.resolve(
        jsonResponse({ id: 'job-1', status: 'completed', importStatus: 'completed', errorMessage: null, createdAtUtc: '', completedAtUtc: '' }),
      )
    }))
    vi.stubGlobal('fetch', fetchMock)
    render(<SmartPlugImportPage onBack={() => {}} />)

    await selectFiles(makeFile('export.xlsx'))
    await waitFor(() => expect(screen.getByText('Processing')).toBeInTheDocument())
    expect(screen.getByText(/parsing this in the background/)).toBeInTheDocument()

    await vi.advanceTimersByTimeAsync(2000)

    await waitFor(() => expect(screen.getByText('Import complete')).toBeInTheDocument())
    expect(screen.queryByText(/parsing this in the background/)).not.toBeInTheDocument()
  })

  it('shows the failed state when the job reaches a failed status', async () => {
    const fetchMock = vi.fn(withJobHistoryStub((url) => {
      if (url === '/api/smart-plug-imports') {
        return Promise.resolve(jsonResponse({ jobId: 'job-1' }, 202))
      }

      return Promise.resolve(
        jsonResponse({ id: 'job-1', status: 'failed', errorMessage: 'boom', createdAtUtc: '', completedAtUtc: '' }),
      )
    }))
    vi.stubGlobal('fetch', fetchMock)
    render(<SmartPlugImportPage onBack={() => {}} />)

    await selectFiles(makeFile('export.xlsx'))
    await waitFor(() => expect(screen.getByText('Processing')).toBeInTheDocument())

    await vi.advanceTimersByTimeAsync(2000)

    await waitFor(() => expect(screen.getByText('Import failed')).toBeInTheDocument())
    expect(screen.getByText('boom')).toBeInTheDocument()
  })

  it('shows a needs-mapping badge and the create/map dialog when the import completes without a Power Point match', async () => {
    const fetchMock = vi.fn(withJobHistoryStub((url) => {
      if (url === '/api/smart-plug-imports') {
        return Promise.resolve(jsonResponse({ jobId: 'job-1' }, 202))
      }
      if (url === '/api/jobs/job-1') {
        return Promise.resolve(
          jsonResponse({
            id: 'job-1',
            status: 'completed',
            importStatus: 'awaitingpowerpointmapping',
            errorMessage: null,
            createdAtUtc: '',
            completedAtUtc: '',
            smartPlugImportId: 'import-1',
            smartPlugImportDeviceTag: 'Office Desk',
          }),
        )
      }
      if (url === '/api/rooms') {
        return Promise.resolve(jsonResponse([{ id: 'room-1', name: 'Living room', archivedAt: null }]))
      }
      if (url === '/api/power-points') {
        return Promise.resolve(jsonResponse([]))
      }

      throw new Error(`Unexpected fetch: ${url}`)
    }))
    vi.stubGlobal('fetch', fetchMock)
    render(<SmartPlugImportPage onBack={() => {}} />)

    await selectFiles(makeFile('export.xlsx'))
    await waitFor(() => expect(screen.getByText('Processing')).toBeInTheDocument())

    await vi.advanceTimersByTimeAsync(2000)

    await waitFor(() => expect(screen.getByText('Needs Power Point mapping')).toBeInTheDocument())
    await waitFor(() => expect(screen.getByText('New Power Point: "Office Desk"')).toBeInTheDocument())
  })

  it('flips to the completed state once the mapping dialog reports success', async () => {
    const fetchMock = vi.fn(withJobHistoryStub((url, init) => {
      if (url === '/api/smart-plug-imports') {
        return Promise.resolve(jsonResponse({ jobId: 'job-1' }, 202))
      }
      if (url === '/api/jobs/job-1') {
        return Promise.resolve(
          jsonResponse({
            id: 'job-1',
            status: 'completed',
            importStatus: 'awaitingpowerpointmapping',
            errorMessage: null,
            createdAtUtc: '',
            completedAtUtc: '',
            smartPlugImportId: 'import-1',
            smartPlugImportDeviceTag: 'Office Desk',
          }),
        )
      }
      if (url === '/api/rooms') {
        return Promise.resolve(jsonResponse([{ id: 'room-1', name: 'Living room', archivedAt: null }]))
      }
      if (url === '/api/power-points') {
        return Promise.resolve(jsonResponse([{ id: 'pp-1', roomId: 'room-1', name: 'Desk lamp', archivedAt: null }]))
      }
      if (url === '/api/smart-plug-imports/import-1/power-point-mapping') {
        return Promise.resolve(jsonResponse({ id: 'import-1', status: 'completed' }))
      }

      throw new Error(`Unexpected fetch: ${url} ${init?.method ?? ''}`)
    }))
    vi.stubGlobal('fetch', fetchMock)
    render(<SmartPlugImportPage onBack={() => {}} />)

    await selectFiles(makeFile('export.xlsx'))
    await waitFor(() => expect(screen.getByText('Processing')).toBeInTheDocument())
    await vi.advanceTimersByTimeAsync(2000)
    await waitFor(() => expect(screen.getByText('Living room → Desk lamp')).toBeInTheDocument())

    await userEvent.click(screen.getByText('Living room → Desk lamp'))

    await waitFor(() => expect(screen.getByText('Import complete')).toBeInTheDocument())
  })

  it('tolerates a transient polling failure and keeps polling instead of failing immediately', async () => {
    let statusCallCount = 0
    const fetchMock = vi.fn(withJobHistoryStub((url) => {
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
    }))
    vi.stubGlobal('fetch', fetchMock)
    render(<SmartPlugImportPage onBack={() => {}} />)

    await selectFiles(makeFile('export.xlsx'))
    await waitFor(() => expect(screen.getByText('Processing')).toBeInTheDocument())

    await vi.advanceTimersByTimeAsync(2000)
    expect(screen.queryByText('Import failed')).not.toBeInTheDocument()

    await vi.advanceTimersByTimeAsync(2000)
    await waitFor(() => expect(screen.getByText('Import complete')).toBeInTheDocument())
  })

  it('shows a Waiting badge (not Processing) while repeatedly 404ing behind another import, then completes', async () => {
    let statusCallCount = 0
    const fetchMock = vi.fn(withJobHistoryStub((url) => {
      if (url === '/api/smart-plug-imports') {
        return Promise.resolve(jsonResponse({ jobId: 'job-1' }, 202))
      }

      statusCallCount += 1
      if (statusCallCount <= 5) {
        return Promise.resolve(jsonResponse({ detail: "No job 'job-1' found." }, 404))
      }

      return Promise.resolve(
        jsonResponse({ id: 'job-1', status: 'completed', importStatus: 'completed', errorMessage: null, createdAtUtc: '', completedAtUtc: '' }),
      )
    }))
    vi.stubGlobal('fetch', fetchMock)
    render(<SmartPlugImportPage onBack={() => {}} />)

    await selectFiles(makeFile('export.xlsx'))
    await waitFor(() => expect(screen.getByText('Processing')).toBeInTheDocument())

    await vi.advanceTimersByTimeAsync(2000 * 5)
    expect(screen.queryByText('Import failed')).not.toBeInTheDocument()
    expect(screen.getByText('Waiting')).toBeInTheDocument()
    expect(screen.queryByText('Processing')).not.toBeInTheDocument()
    expect(screen.getByText('Still queued — large files can take a while to start processing.')).toBeInTheDocument()

    await vi.advanceTimersByTimeAsync(2000)
    await waitFor(() => expect(screen.getByText('Import complete')).toBeInTheDocument())
  })

  it('shows a Waiting badge (not Processing) for a status:"queued" response, then completes', async () => {
    // Review-round-2 patch (Story 3.6): the backend now inserts a Queued row at enqueue time, so
    // GET /api/jobs/{id} returns 200 status:'queued' instead of 404 for a job not yet dequeued —
    // companion to the 404-based test above, covering the new (now-primary) path directly.
    let statusCallCount = 0
    const fetchMock = vi.fn(withJobHistoryStub((url) => {
      if (url === '/api/smart-plug-imports') {
        return Promise.resolve(jsonResponse({ jobId: 'job-1' }, 202))
      }

      statusCallCount += 1
      if (statusCallCount <= 5) {
        return Promise.resolve(
          jsonResponse({ id: 'job-1', status: 'queued', importStatus: null, errorMessage: null, createdAtUtc: '', completedAtUtc: null }),
        )
      }

      return Promise.resolve(
        jsonResponse({ id: 'job-1', status: 'completed', importStatus: 'completed', errorMessage: null, createdAtUtc: '', completedAtUtc: '' }),
      )
    }))
    vi.stubGlobal('fetch', fetchMock)
    render(<SmartPlugImportPage onBack={() => {}} />)

    await selectFiles(makeFile('export.xlsx'))
    await waitFor(() => expect(screen.getByText('Processing')).toBeInTheDocument())

    await vi.advanceTimersByTimeAsync(2000 * 5)
    expect(screen.queryByText('Import failed')).not.toBeInTheDocument()
    expect(screen.getByText('Waiting')).toBeInTheDocument()
    expect(screen.queryByText('Processing')).not.toBeInTheDocument()
    expect(screen.getByText('Still queued — large files can take a while to start processing.')).toBeInTheDocument()

    await vi.advanceTimersByTimeAsync(2000)
    await waitFor(() => expect(screen.getByText('Import complete')).toBeInTheDocument())
  })

  it('shows an error and keeps the row when the upload itself is rejected (unsupported type)', async () => {
    vi.stubGlobal('fetch', vi.fn(withJobHistoryStub(() => Promise.resolve(jsonResponse({ detail: 'bad type' }, 400)))))
    render(<SmartPlugImportPage onBack={() => {}} />)

    await selectFiles(makeFile('notes.csv'))

    await waitFor(() =>
      expect(screen.getByText("That file type isn't supported. Please choose an .xlsx or .csv export.")).toBeInTheDocument(),
    )
    expect(screen.getByText('Import failed')).toBeInTheDocument()
  })

  it('shows the flagged-for-review state and its gap card when the import is entirely gaps', async () => {
    const fetchMock = vi.fn(withJobHistoryStub((url) => {
      if (url === '/api/smart-plug-imports') {
        return Promise.resolve(jsonResponse({ jobId: 'job-1' }, 202))
      }

      return Promise.resolve(
        jsonResponse({
          id: 'job-1',
          status: 'completed',
          importStatus: 'flaggedforreview',
          errorMessage: null,
          createdAtUtc: '',
          completedAtUtc: '',
          gaps: [{ startDate: '2026-08-01', endDate: '2026-08-09', treatment: 'flaggedforreview', estimatedTotalKwh: null }],
        }),
      )
    }))
    vi.stubGlobal('fetch', fetchMock)
    render(<SmartPlugImportPage onBack={() => {}} />)

    await selectFiles(makeFile('empty.csv'))
    await waitFor(() => expect(screen.getByText('Processing')).toBeInTheDocument())

    await vi.advanceTimersByTimeAsync(2000)

    await waitFor(() => expect(screen.getByText("Needs a look before it's used")).toBeInTheDocument())
    expect(screen.getByText('Flagged for review')).toBeInTheDocument()
  })

  it('renders gap cards in the completed state when the job carries gaps', async () => {
    const fetchMock = vi.fn(withJobHistoryStub((url) => {
      if (url === '/api/smart-plug-imports') {
        return Promise.resolve(jsonResponse({ jobId: 'job-1' }, 202))
      }

      return Promise.resolve(
        jsonResponse({
          id: 'job-1',
          status: 'completed',
          importStatus: 'completed',
          errorMessage: null,
          createdAtUtc: '',
          completedAtUtc: '',
          gaps: [{ startDate: '2026-04-12', endDate: '2026-04-17', treatment: 'estimated', estimatedTotalKwh: 24.6 }],
        }),
      )
    }))
    vi.stubGlobal('fetch', fetchMock)
    render(<SmartPlugImportPage onBack={() => {}} />)

    await selectFiles(makeFile('export.xlsx'))
    await waitFor(() => expect(screen.getByText('Processing')).toBeInTheDocument())

    await vi.advanceTimersByTimeAsync(2000)

    await waitFor(() => expect(screen.getByText('Import complete')).toBeInTheDocument())
    expect(screen.getByText('Estimated')).toBeInTheDocument()
  })

  it('removes a completed item from the queue when "Remove from queue" is tapped', async () => {
    const fetchMock = vi.fn(withJobHistoryStub((url) => {
      if (url === '/api/smart-plug-imports') {
        return Promise.resolve(jsonResponse({ jobId: 'job-1' }, 202))
      }

      return Promise.resolve(
        jsonResponse({ id: 'job-1', status: 'completed', importStatus: 'completed', errorMessage: null, createdAtUtc: '', completedAtUtc: '' }),
      )
    }))
    vi.stubGlobal('fetch', fetchMock)
    render(<SmartPlugImportPage onBack={() => {}} />)

    await selectFiles(makeFile('export.xlsx'))
    await waitFor(() => expect(screen.getByText('Processing')).toBeInTheDocument())
    await vi.advanceTimersByTimeAsync(2000)
    await waitFor(() => expect(screen.getByText('Import complete')).toBeInTheDocument())

    await userEvent.click(screen.getByRole('button', { name: 'Remove from queue' }))

    expect(screen.queryByText('export.xlsx')).not.toBeInTheDocument()
  })

  it('shows "Add more files" instead of the initial hint once a file has already been queued', async () => {
    vi.stubGlobal('fetch', vi.fn(withJobHistoryStub(() => Promise.resolve(jsonResponse({ jobId: 'job-1' }, 202)))))
    render(<SmartPlugImportPage onBack={() => {}} />)

    await selectFiles(makeFile('export.xlsx'))

    await waitFor(() => expect(screen.getByText('Add more files')).toBeInTheDocument())
    expect(screen.queryByText('Drop a file here, or choose one to upload.')).not.toBeInTheDocument()
  })

  it('clears each item\'s polling interval on unmount', async () => {
    const fetchMock = vi.fn(withJobHistoryStub((url) => {
      if (url === '/api/smart-plug-imports') {
        return Promise.resolve(jsonResponse({ jobId: 'job-1' }, 202))
      }

      return Promise.resolve(
        jsonResponse({ id: 'job-1', status: 'processing', errorMessage: null, createdAtUtc: '', completedAtUtc: null }),
      )
    }))
    vi.stubGlobal('fetch', fetchMock)
    const { unmount } = render(<SmartPlugImportPage onBack={() => {}} />)

    await selectFiles(makeFile('export.xlsx'))
    await waitFor(() => expect(screen.getByText('Processing')).toBeInTheDocument())

    const callCountAtUnmount = fetchMock.mock.calls.length
    unmount()

    await vi.advanceTimersByTimeAsync(10000)

    expect(fetchMock.mock.calls.length).toBe(callCountAtUnmount)
  })
})
