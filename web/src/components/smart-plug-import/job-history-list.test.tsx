import { render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { JobHistoryList } from './job-history-list'
import type { SmartPlugImportJobDto } from '@/lib/smart-plug-import-api'

function jsonResponse(body: unknown, status = 200) {
  return new Response(JSON.stringify(body), { status })
}

const BASE_JOB: SmartPlugImportJobDto = {
  jobId: 'job-1',
  fileName: 'EveHome-FridgeCircuit-Aug2026.xlsx',
  state: 'success',
  queuedByDisplayName: 'Mira',
  queuedAtUtc: '2026-08-07T12:00:00Z',
  completedAtUtc: '2026-08-07T12:05:00Z',
  errorMessage: null,
  smartPlugImportId: null,
  deviceTag: null,
  gaps: [],
}

function makeJob(overrides: Partial<SmartPlugImportJobDto>): SmartPlugImportJobDto {
  return { ...BASE_JOB, jobId: overrides.jobId ?? crypto.randomUUID(), ...overrides }
}

// Round-3 incident fix (2026-09-12): cleanup now enqueues an async job (202 + jobId) and the
// component polls GET /api/jobs/{jobId} for completion instead of the DELETE response carrying a
// synchronous deletedCount.
const CLEANUP_JOB_ID = 'cleanup-job-1'

function jobStatusResponse(overrides: Partial<{ status: string; errorMessage: string | null }> = {}) {
  return jsonResponse({
    id: CLEANUP_JOB_ID,
    status: overrides.status ?? 'completed',
    importStatus: null,
    errorMessage: overrides.errorMessage ?? null,
    createdAtUtc: '2026-08-07T12:00:00Z',
    completedAtUtc: '2026-08-07T12:00:01Z',
    smartPlugImportId: null,
    smartPlugImportDeviceTag: null,
    gaps: [],
  })
}

function stubFetch(
  jobs: SmartPlugImportJobDto[],
  options: {
    jobsAfterCleanup?: SmartPlugImportJobDto[]
    cleanupJobStatus?: string
    cleanupErrorMessage?: string
    cleanupPollFailuresBeforeSuccess?: number
  } = {},
) {
  let currentJobs = jobs
  let cleanupPollAttempts = 0
  const fetchMock = vi.fn((url: string, init?: RequestInit) => {
    if (url === '/api/smart-plug-import-jobs' && (!init || init.method === undefined)) {
      return Promise.resolve(jsonResponse(currentJobs))
    }
    if (url.startsWith('/api/smart-plug-import-jobs?deleteAll=') && init?.method === 'DELETE') {
      if (options.jobsAfterCleanup) {
        currentJobs = options.jobsAfterCleanup
      }
      return Promise.resolve(jsonResponse({ jobId: CLEANUP_JOB_ID }, 202))
    }
    if (url === `/api/jobs/${CLEANUP_JOB_ID}`) {
      cleanupPollAttempts += 1
      if (options.cleanupPollFailuresBeforeSuccess && cleanupPollAttempts <= options.cleanupPollFailuresBeforeSuccess) {
        return Promise.resolve(new Response(null, { status: 503 }))
      }
      return Promise.resolve(jobStatusResponse({ status: options.cleanupJobStatus, errorMessage: options.cleanupErrorMessage }))
    }
    if (url === '/api/rooms') {
      return Promise.resolve(jsonResponse([{ id: 'room-1', name: 'Living room', archivedAt: null }]))
    }
    if (url === '/api/power-points') {
      return Promise.resolve(jsonResponse([{ id: 'pp-1', roomId: 'room-1', name: 'Office Desk', archivedAt: null }]))
    }

    throw new Error(`Unexpected fetch: ${url} ${init?.method ?? 'GET'}`)
  })
  vi.stubGlobal('fetch', fetchMock)
  return fetchMock
}

describe('JobHistoryList', () => {
  afterEach(() => {
    vi.unstubAllGlobals()
  })

  it('renders all six badge states from a mocked fetch response', async () => {
    const jobs = [
      makeJob({ jobId: 'waiting', state: 'waiting', fileName: 'a.csv' }),
      makeJob({ jobId: 'processing', state: 'processing', fileName: 'b.csv' }),
      makeJob({ jobId: 'success', state: 'success', fileName: 'c.csv' }),
      makeJob({ jobId: 'error', state: 'error', fileName: 'd.csv', errorMessage: "Couldn't be read as a Meross export" }),
      makeJob({ jobId: 'needsMapping', state: 'needsMapping', fileName: 'e.csv', smartPlugImportId: 'import-1', deviceTag: 'Office Desk' }),
      makeJob({ jobId: 'flaggedForReview', state: 'flaggedForReview', fileName: 'f.csv' }),
    ]
    stubFetch(jobs)

    render(<JobHistoryList />)

    await waitFor(() => expect(screen.getByText('a.csv')).toBeInTheDocument())
    expect(screen.getByText('Waiting')).toBeInTheDocument()
    expect(screen.getByText('Processing')).toBeInTheDocument()
    expect(screen.getByText('Success')).toBeInTheDocument()
    expect(screen.getByText('Error')).toBeInTheDocument()
    expect(screen.getByText('Needs Mapping')).toBeInTheDocument()
    expect(screen.getByText('Flagged for Review')).toBeInTheDocument()
  })

  it('renders the empty state, not blank space or an error, when the fetched list is empty', async () => {
    stubFetch([])

    render(<JobHistoryList />)

    await waitFor(() => expect(screen.getByText('No imports yet')).toBeInTheDocument())
    expect(screen.getByText(/Nothing uploaded in the last 30 days/)).toBeInTheDocument()
  })

  it('opens PowerPointMappingDialog with the row\'s smartPlugImportId/deviceTag on a Needs Mapping tap', async () => {
    const job = makeJob({
      jobId: 'needsMapping', state: 'needsMapping', fileName: 'e.csv',
      smartPlugImportId: 'import-1', deviceTag: 'Office Desk',
    })
    stubFetch([job])

    render(<JobHistoryList />)

    await waitFor(() => expect(screen.getByText('e.csv')).toBeInTheDocument())
    await userEvent.click(screen.getByRole('button', { name: 'Map e.csv' }))

    await waitFor(() => expect(screen.getByText('New Power Point: "Office Desk"')).toBeInTheDocument())
  })

  it('reveals GapCard on a Flagged for Review row tap', async () => {
    const job = makeJob({
      jobId: 'flaggedForReview',
      state: 'flaggedForReview',
      fileName: 'f.csv',
      gaps: [{ startDate: '2026-08-01', endDate: '2026-08-09', treatment: 'flaggedforreview', estimatedTotalKwh: null }],
    })
    stubFetch([job])

    render(<JobHistoryList />)

    await waitFor(() => expect(screen.getByText('f.csv')).toBeInTheDocument())
    expect(screen.queryByText('Flagged for review')).not.toBeInTheDocument()

    await userEvent.click(screen.getByRole('button', { name: 'Review f.csv' }))

    await waitFor(() => expect(screen.getByText('Flagged for review')).toBeInTheDocument())
  })

  it('animates only the Processing row\'s icon, not other states', async () => {
    const jobs = [
      makeJob({ jobId: 'waiting', state: 'waiting', fileName: 'a.csv' }),
      makeJob({ jobId: 'processing', state: 'processing', fileName: 'b.csv' }),
      makeJob({ jobId: 'success', state: 'success', fileName: 'c.csv' }),
    ]
    stubFetch(jobs)

    render(<JobHistoryList />)
    await waitFor(() => expect(screen.getByText('b.csv')).toBeInTheDocument())

    const processingRow = screen.getByText('b.csv').closest('.border-b')
    expect(processingRow?.querySelector('svg')).toHaveClass('animate-spin')

    const waitingRow = screen.getByText('a.csv').closest('.border-b')
    expect(waitingRow?.querySelector('svg')).not.toHaveClass('animate-spin')

    const successRow = screen.getByText('c.csv').closest('.border-b')
    expect(successRow?.querySelector('svg')).not.toHaveClass('animate-spin')
  })

  it('renders the fallback string, never blank/undefined, when queuedByDisplayName is null', async () => {
    const job = makeJob({ queuedByDisplayName: null })
    stubFetch([job])

    render(<JobHistoryList />)

    await waitFor(() => expect(screen.getByText(/Queued by a household member/)).toBeInTheDocument())
  })

  describe('Clean up history (Story 3.10)', () => {
    it('opens the confirmation dialog on click, never deletes on a bare click', async () => {
      const job = makeJob({ jobId: 'a', fileName: 'a.csv' })
      const fetchMock = stubFetch([job])

      render(<JobHistoryList />)
      await waitFor(() => expect(screen.getByText('a.csv')).toBeInTheDocument())
      await userEvent.click(screen.getByRole('button', { name: 'Clean up history' }))

      await waitFor(() => expect(screen.getByText('Clean up import history')).toBeInTheDocument())
      expect(fetchMock).not.toHaveBeenCalledWith(expect.stringContaining('deleteAll'), expect.anything())
    })

    it('defaults to the older-than-30-days option', async () => {
      const job = makeJob({ jobId: 'a', fileName: 'a.csv' })
      stubFetch([job])

      render(<JobHistoryList />)
      await waitFor(() => expect(screen.getByText('a.csv')).toBeInTheDocument())
      await userEvent.click(screen.getByRole('button', { name: 'Clean up history' }))

      await waitFor(() => expect(screen.getByText('Clean up import history')).toBeInTheDocument())
      expect(screen.getByRole('radio', { name: /Delete entries older than 30 days/ })).toBeChecked()
      expect(screen.getByRole('radio', { name: /Delete everything/ })).not.toBeChecked()
    })

    it('confirming the default option calls cleanUpSmartPlugImportJobs with deleteAll=false and re-fetches the list', async () => {
      const job = makeJob({ jobId: 'a', fileName: 'a.csv' })
      const fetchMock = stubFetch([job], { jobsAfterCleanup: [] })

      render(<JobHistoryList />)
      await waitFor(() => expect(screen.getByText('a.csv')).toBeInTheDocument())
      await userEvent.click(screen.getByRole('button', { name: 'Clean up history' }))
      await waitFor(() => expect(screen.getByText('Clean up import history')).toBeInTheDocument())
      await userEvent.click(screen.getByRole('button', { name: 'Delete' }))

      await waitFor(() =>
        expect(fetchMock).toHaveBeenCalledWith('/api/smart-plug-import-jobs?deleteAll=false', expect.objectContaining({ method: 'DELETE' })),
      )
      await waitFor(() => expect(screen.getByText('No imports yet')).toBeInTheDocument())
    })

    it('confirming the everything option calls cleanUpSmartPlugImportJobs with deleteAll=true', async () => {
      const job = makeJob({ jobId: 'a', fileName: 'a.csv' })
      const fetchMock = stubFetch([job], { jobsAfterCleanup: [] })

      render(<JobHistoryList />)
      await waitFor(() => expect(screen.getByText('a.csv')).toBeInTheDocument())
      await userEvent.click(screen.getByRole('button', { name: 'Clean up history' }))
      await waitFor(() => expect(screen.getByText('Clean up import history')).toBeInTheDocument())
      await userEvent.click(screen.getByRole('radio', { name: /Delete everything/ }))
      await userEvent.click(screen.getByRole('button', { name: 'Delete' }))

      await waitFor(() =>
        expect(fetchMock).toHaveBeenCalledWith('/api/smart-plug-import-jobs?deleteAll=true', expect.objectContaining({ method: 'DELETE' })),
      )
    })

    it('surfaces the error and keeps the dialog open when the accepted cleanup job later fails', async () => {
      // Round-3 incident fix (2026-09-12): the DELETE call itself can succeed (202 + jobId) while
      // the background job it enqueued later fails — distinct from the "DELETE request itself
      // rejected" case the test below this one covers.
      const job = makeJob({ jobId: 'a', fileName: 'a.csv' })
      stubFetch([job], { cleanupJobStatus: 'failed', cleanupErrorMessage: 'An unexpected error occurred while cleaning up the history.' })

      render(<JobHistoryList />)
      await waitFor(() => expect(screen.getByText('a.csv')).toBeInTheDocument())
      await userEvent.click(screen.getByRole('button', { name: 'Clean up history' }))
      await waitFor(() => expect(screen.getByText('Clean up import history')).toBeInTheDocument())
      await userEvent.click(screen.getByRole('button', { name: 'Delete' }))

      await waitFor(() => expect(screen.getByText('An unexpected error occurred while cleaning up the history.')).toBeInTheDocument())
      expect(screen.getByText('Clean up import history')).toBeInTheDocument()
      expect(screen.getByText('a.csv')).toBeInTheDocument()
    })

    it('tolerates one transient poll failure and still completes the cleanup', async () => {
      // Round-3 review fix: a single dropped fetch (network blip/transient 5xx) while polling for
      // cleanup completion must not report failure — same MAX_CONSECUTIVE_POLL_FAILURES tolerance
      // useSmartPlugImportJob.ts already applies to its own polling.
      vi.useFakeTimers({ shouldAdvanceTime: true })
      try {
        const job = makeJob({ jobId: 'a', fileName: 'a.csv' })
        stubFetch([job], { jobsAfterCleanup: [], cleanupPollFailuresBeforeSuccess: 1 })

        render(<JobHistoryList />)
        await waitFor(() => expect(screen.getByText('a.csv')).toBeInTheDocument())
        await userEvent.click(screen.getByRole('button', { name: 'Clean up history' }))
        await waitFor(() => expect(screen.getByText('Clean up import history')).toBeInTheDocument())
        await userEvent.click(screen.getByRole('button', { name: 'Delete' }))

        await vi.advanceTimersByTimeAsync(2000)

        await waitFor(() => expect(screen.queryByText('Clean up import history')).not.toBeInTheDocument())
        expect(screen.queryByText('a.csv')).not.toBeInTheDocument()
      } finally {
        vi.useRealTimers()
      }
    })

    it('cancelling closes the dialog with no API call', async () => {
      const job = makeJob({ jobId: 'a', fileName: 'a.csv' })
      const fetchMock = stubFetch([job])

      render(<JobHistoryList />)
      await waitFor(() => expect(screen.getByText('a.csv')).toBeInTheDocument())
      await userEvent.click(screen.getByRole('button', { name: 'Clean up history' }))
      await waitFor(() => expect(screen.getByText('Clean up import history')).toBeInTheDocument())
      await userEvent.click(screen.getByRole('button', { name: 'Cancel' }))

      await waitFor(() => expect(screen.queryByText('Clean up import history')).not.toBeInTheDocument())
      expect(fetchMock).not.toHaveBeenCalledWith(expect.stringContaining('deleteAll'), expect.anything())
    })

    it('resets to the older-than-30-days option when reopened after selecting everything and cancelling', async () => {
      // Code-review regression guard: the selection used to survive Cancel and persist into the
      // next open, which could silently pre-select "everything" on a later, unrelated cleanup.
      const job = makeJob({ jobId: 'a', fileName: 'a.csv' })
      stubFetch([job])

      render(<JobHistoryList />)
      await waitFor(() => expect(screen.getByText('a.csv')).toBeInTheDocument())
      await userEvent.click(screen.getByRole('button', { name: 'Clean up history' }))
      await waitFor(() => expect(screen.getByText('Clean up import history')).toBeInTheDocument())
      await userEvent.click(screen.getByRole('radio', { name: /Delete everything/ }))
      await userEvent.click(screen.getByRole('button', { name: 'Cancel' }))
      await waitFor(() => expect(screen.queryByText('Clean up import history')).not.toBeInTheDocument())

      await userEvent.click(screen.getByRole('button', { name: 'Clean up history' }))

      await waitFor(() => expect(screen.getByRole('radio', { name: /Delete entries older than 30 days/ })).toBeChecked())
      expect(screen.getByRole('radio', { name: /Delete everything/ })).not.toBeChecked()
    })

    it('clears a previous error message when the dialog is reopened', async () => {
      // Code-review regression guard: a stale error from a prior failed attempt used to remain
      // visible on the next open, before any new request had even been made.
      const job = makeJob({ jobId: 'a', fileName: 'a.csv' })
      const fetchMock = vi.fn((url: string, init?: RequestInit) => {
        if (url === '/api/smart-plug-import-jobs' && (!init || init.method === undefined)) {
          return Promise.resolve(jsonResponse([job]))
        }
        if (url.startsWith('/api/smart-plug-import-jobs?deleteAll=') && init?.method === 'DELETE') {
          return Promise.resolve(jsonResponse({ detail: 'Something went wrong' }, 500))
        }
        throw new Error(`Unexpected fetch: ${url} ${init?.method ?? 'GET'}`)
      })
      vi.stubGlobal('fetch', fetchMock)

      render(<JobHistoryList />)
      await waitFor(() => expect(screen.getByText('a.csv')).toBeInTheDocument())
      await userEvent.click(screen.getByRole('button', { name: 'Clean up history' }))
      await waitFor(() => expect(screen.getByText('Clean up import history')).toBeInTheDocument())
      await userEvent.click(screen.getByRole('button', { name: 'Delete' }))
      await waitFor(() => expect(screen.getByText('Something went wrong')).toBeInTheDocument())
      await userEvent.click(screen.getByRole('button', { name: 'Cancel' }))
      await waitFor(() => expect(screen.queryByText('Clean up import history')).not.toBeInTheDocument())

      await userEvent.click(screen.getByRole('button', { name: 'Clean up history' }))

      await waitFor(() => expect(screen.getByText('Clean up import history')).toBeInTheDocument())
      expect(screen.queryByText('Something went wrong')).not.toBeInTheDocument()
    })
  })
})
