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

function stubFetch(jobs: SmartPlugImportJobDto[]) {
  const fetchMock = vi.fn((url: string) => {
    if (url === '/api/smart-plug-import-jobs') {
      return Promise.resolve(jsonResponse(jobs))
    }
    if (url === '/api/rooms') {
      return Promise.resolve(jsonResponse([{ id: 'room-1', name: 'Living room', archivedAt: null }]))
    }
    if (url === '/api/power-points') {
      return Promise.resolve(jsonResponse([{ id: 'pp-1', roomId: 'room-1', name: 'Office Desk', archivedAt: null }]))
    }

    throw new Error(`Unexpected fetch: ${url}`)
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

  it('renders the fallback string, never blank/undefined, when queuedByDisplayName is null', async () => {
    const job = makeJob({ queuedByDisplayName: null })
    stubFetch([job])

    render(<JobHistoryList />)

    await waitFor(() => expect(screen.getByText(/Queued by a household member/)).toBeInTheDocument())
  })
})
