import { render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { DataImportPanel } from './data-import-panel'

function jsonResponse(body: object | null, status = 200) {
  return new Response(body === null ? null : JSON.stringify(body), { status })
}

function makeFile(name = 'export.json') {
  return new File(['{"formatVersion":"v2"}'], name, { type: 'application/json' })
}

async function selectFile(file: File) {
  const input = document.querySelector('input[type="file"]') as HTMLInputElement
  await userEvent.upload(input, file)
}

const validationResponseBody = {
  token: 'aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee',
  summary: {
    householdMembers: 1,
    hasMainMeter: true,
    meterReadings: 5,
    meterRegressionPrompts: 0,
    tariffs: 1,
    events: 2,
    rooms: 1,
    powerPoints: 1,
    devices: 0,
    smartPlugReadings: 0,
    statusSnapshots: 3,
    auditCorrections: 0,
  },
}

describe('DataImportPanel', () => {
  beforeEach(() => {
    vi.useFakeTimers({ shouldAdvanceTime: true })
  })

  afterEach(() => {
    vi.unstubAllGlobals()
    vi.useRealTimers()
  })

  // UX-DR14: "Import data fails validation" — rejected and reported with what failed, never a
  // generic error.
  it('renders every reported validation failure on a 400 response', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn(() =>
        Promise.resolve(
          jsonResponse(
            { detail: 'The uploaded file failed validation.', failures: ['formatVersion must be v2', 'household.currency must be a string'] },
            400,
          ),
        ),
      ),
    )

    render(<DataImportPanel />)
    await selectFile(makeFile())

    expect(await screen.findByText(/failed validation/i)).toBeInTheDocument()
    expect(await screen.findByText('formatVersion must be v2')).toBeInTheDocument()
    expect(screen.getByText('household.currency must be a string')).toBeInTheDocument()
  })

  // Code review, Story 7.2 Pass 2: a 400 response body can only be read once — a bug that read it
  // twice (once for `failures`, again in the ApiError fallback) silently discarded the server's
  // real `detail` message for any 400 not shaped as the canonical validation-failures payload
  // (e.g. the file-too-large response), falling back to the generic error text instead.
  it('shows the servers own detail message for a 400 response with no failures array', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn(() => Promise.resolve(jsonResponse({ detail: 'File is too large (300000000 bytes).' }, 400))),
    )

    render(<DataImportPanel />)
    await selectFile(makeFile())

    expect(await screen.findByText('File is too large (300000000 bytes).')).toBeInTheDocument()
  })

  it('shows the confirmation dialog with the entity-count summary after a valid upload', async () => {
    vi.stubGlobal('fetch', vi.fn(() => Promise.resolve(jsonResponse(validationResponseBody))))

    render(<DataImportPanel />)
    await selectFile(makeFile())

    expect(await screen.findByText(/replace all data with this file/i)).toBeInTheDocument()
    expect(screen.getByText('1 Household Member')).toBeInTheDocument()
    expect(screen.getByText('A Main Meter')).toBeInTheDocument()
    expect(screen.getByText('5 Meter Readings')).toBeInTheDocument()
    expect(screen.getByText('2 Events')).toBeInTheDocument()
    expect(screen.getByText('3 Status entries')).toBeInTheDocument()
  })

  it('cancelling the confirmation dialog returns to idle without confirming', async () => {
    vi.stubGlobal('fetch', vi.fn(() => Promise.resolve(jsonResponse(validationResponseBody))))

    render(<DataImportPanel />)
    await selectFile(makeFile())
    await screen.findByText(/replace all data with this file/i)

    const user = userEvent.setup({ advanceTimers: vi.advanceTimersByTime })
    await user.click(screen.getByRole('button', { name: 'Cancel' }))

    expect(screen.queryByText(/replace all data with this file/i)).not.toBeInTheDocument()
    expect(screen.getByRole('button', { name: /choose file to import/i })).toBeInTheDocument()
  })

  // Full flow: upload -> confirm -> poll -> success (AC #4, #1).
  it('confirming polls the restore job and shows success on completion', async () => {
    const fetchMock = vi.fn((input: string | URL | Request) => {
      const url = String(input)
      if (url === '/api/household-import') {
        return Promise.resolve(jsonResponse(validationResponseBody))
      }
      if (url.endsWith('/confirm')) {
        return Promise.resolve(jsonResponse({ jobId: 'job-1' }, 202))
      }
      if (url === '/api/jobs/job-1') {
        return Promise.resolve(jsonResponse({ id: 'job-1', status: 'processing', importStatus: null, errorMessage: null }))
      }
      throw new Error(`Unexpected fetch: ${url}`)
    })
    vi.stubGlobal('fetch', fetchMock)

    render(<DataImportPanel />)
    await selectFile(makeFile())
    await screen.findByText(/replace all data with this file/i)

    const user = userEvent.setup({ advanceTimers: vi.advanceTimersByTime })
    await user.click(screen.getByRole('button', { name: 'Replace all data' }))

    await screen.findByText(/restoring/i)

    fetchMock.mockImplementation((input: string | URL | Request) => {
      const url = String(input)
      if (url === '/api/jobs/job-1') {
        return Promise.resolve(jsonResponse({ id: 'job-1', status: 'completed', importStatus: null, errorMessage: null }))
      }
      throw new Error(`Unexpected fetch: ${url}`)
    })
    await vi.advanceTimersByTimeAsync(2000)

    expect(await screen.findByText(/restore complete/i)).toBeInTheDocument()
  })

  // The polled job itself can fail (a real DB error, a missing temp file race) — must render as
  // an explicit failure, not silently hang on "restoring".
  it('shows the jobs own error message when the restore job fails', async () => {
    const fetchMock = vi.fn((input: string | URL | Request) => {
      const url = String(input)
      if (url === '/api/household-import') {
        return Promise.resolve(jsonResponse(validationResponseBody))
      }
      if (url.endsWith('/confirm')) {
        return Promise.resolve(jsonResponse({ jobId: 'job-1' }, 202))
      }
      if (url === '/api/jobs/job-1') {
        return Promise.resolve(jsonResponse({ id: 'job-1', status: 'processing', importStatus: null, errorMessage: null }))
      }
      throw new Error(`Unexpected fetch: ${url}`)
    })
    vi.stubGlobal('fetch', fetchMock)

    render(<DataImportPanel />)
    await selectFile(makeFile())
    await screen.findByText(/replace all data with this file/i)

    const user = userEvent.setup({ advanceTimers: vi.advanceTimersByTime })
    await user.click(screen.getByRole('button', { name: 'Replace all data' }))
    await screen.findByText(/restoring/i)

    fetchMock.mockImplementation((input: string | URL | Request) => {
      const url = String(input)
      if (url === '/api/jobs/job-1') {
        return Promise.resolve(jsonResponse({ id: 'job-1', status: 'failed', importStatus: null, errorMessage: 'could not be found — please upload it again' }))
      }
      throw new Error(`Unexpected fetch: ${url}`)
    })
    await vi.advanceTimersByTimeAsync(2000)

    expect(await screen.findByText(/could not be found/i)).toBeInTheDocument()
  })
})
