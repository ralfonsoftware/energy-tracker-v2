import { afterEach, describe, expect, it, vi } from 'vitest'
import { ApiError, fetchJobStatus, uploadSmartPlugFile } from './smart-plug-import-api'

function jsonResponse(body: object | null, status = 200) {
  return new Response(body === null ? null : JSON.stringify(body), { status })
}

describe('uploadSmartPlugFile', () => {
  afterEach(() => {
    vi.unstubAllGlobals()
  })

  it('posts the file as multipart form data and returns the job id', async () => {
    const fetchMock = vi.fn((_url: string, _init?: RequestInit) => Promise.resolve(jsonResponse({ jobId: 'job-1' }, 202)))
    vi.stubGlobal('fetch', fetchMock)
    const file = new File(['data'], 'export.xlsx')

    const jobId = await uploadSmartPlugFile(file)

    expect(jobId).toBe('job-1')
    const [url, init] = fetchMock.mock.calls[0]!
    expect(url).toBe('/api/smart-plug-imports')
    expect(init).toMatchObject({ method: 'POST', credentials: 'include' })
    expect(init?.body).toBeInstanceOf(FormData)
    expect((init!.body as FormData).get('file')).toBe(file)
  })

  it('throws an ApiError on a non-2xx response', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn(() => Promise.resolve(jsonResponse({ detail: 'Unsupported file type' }, 400))),
    )
    const file = new File(['data'], 'export.txt')

    await expect(uploadSmartPlugFile(file)).rejects.toBeInstanceOf(ApiError)
    await expect(uploadSmartPlugFile(file)).rejects.toMatchObject({ status: 400, detail: 'Unsupported file type' })
  })
})

describe('fetchJobStatus', () => {
  afterEach(() => {
    vi.unstubAllGlobals()
  })

  it('returns the parsed JobStatusDto on a 200 response', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn(() =>
        Promise.resolve(
          jsonResponse({
            id: 'job-1',
            status: 'completed',
            importStatus: 'completed',
            errorMessage: null,
            createdAtUtc: '2026-08-18T10:00:00+00:00',
            completedAtUtc: '2026-08-18T10:00:05+00:00',
          }),
        ),
      ),
    )

    const result = await fetchJobStatus('job-1')

    expect(result).toEqual({
      id: 'job-1',
      status: 'completed',
      importStatus: 'completed',
      errorMessage: null,
      createdAtUtc: '2026-08-18T10:00:00+00:00',
      completedAtUtc: '2026-08-18T10:00:05+00:00',
    })
  })

  it('throws an ApiError on a non-2xx response', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn(() => Promise.resolve(jsonResponse({ detail: "No job 'job-1' found." }, 404))),
    )

    await expect(fetchJobStatus('job-1')).rejects.toBeInstanceOf(ApiError)
    await expect(fetchJobStatus('job-1')).rejects.toMatchObject({ status: 404 })
  })
})
