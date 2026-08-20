import { afterEach, describe, expect, it, vi } from 'vitest'
import {
  ApiError,
  createPowerPoint,
  fetchJobStatus,
  fetchPowerPoints,
  fetchRooms,
  mapSmartPlugImportToPowerPoint,
  uploadSmartPlugFile,
} from './smart-plug-import-api'

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

describe('mapSmartPlugImportToPowerPoint', () => {
  afterEach(() => {
    vi.unstubAllGlobals()
  })

  it('posts the Power Point id and resolves on success', async () => {
    const fetchMock = vi.fn((_url: string, _init?: RequestInit) => Promise.resolve(jsonResponse({ id: 'import-1', status: 'completed' })))
    vi.stubGlobal('fetch', fetchMock)

    await mapSmartPlugImportToPowerPoint('import-1', 'pp-1')

    const [url, init] = fetchMock.mock.calls[0]!
    expect(url).toBe('/api/smart-plug-imports/import-1/power-point-mapping')
    expect(init).toMatchObject({ method: 'POST', credentials: 'include' })
    expect(JSON.parse(init!.body as string)).toEqual({ powerPointId: 'pp-1' })
  })

  it('throws an ApiError on a non-2xx response', async () => {
    vi.stubGlobal('fetch', vi.fn(() => Promise.resolve(jsonResponse({ detail: 'Conflict' }, 409))))

    await expect(mapSmartPlugImportToPowerPoint('import-1', 'pp-1')).rejects.toBeInstanceOf(ApiError)
    await expect(mapSmartPlugImportToPowerPoint('import-1', 'pp-1')).rejects.toMatchObject({ status: 409, detail: 'Conflict' })
  })
})

describe('fetchRooms', () => {
  afterEach(() => {
    vi.unstubAllGlobals()
  })

  it('returns the parsed Room list on a 200 response', async () => {
    vi.stubGlobal('fetch', vi.fn(() => Promise.resolve(jsonResponse([{ id: 'room-1', name: 'Kitchen', archivedAt: null }]))))

    const result = await fetchRooms()

    expect(result).toEqual([{ id: 'room-1', name: 'Kitchen', archivedAt: null }])
  })

  it('throws an ApiError on a non-2xx response', async () => {
    vi.stubGlobal('fetch', vi.fn(() => Promise.resolve(jsonResponse(null, 403))))

    await expect(fetchRooms()).rejects.toBeInstanceOf(ApiError)
  })
})

describe('fetchPowerPoints', () => {
  afterEach(() => {
    vi.unstubAllGlobals()
  })

  it('returns the parsed Power Point list on a 200 response', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn(() => Promise.resolve(jsonResponse([{ id: 'pp-1', roomId: 'room-1', name: 'Outlet', archivedAt: null }]))),
    )

    const result = await fetchPowerPoints()

    expect(result).toEqual([{ id: 'pp-1', roomId: 'room-1', name: 'Outlet', archivedAt: null }])
  })

  it('throws an ApiError on a non-2xx response', async () => {
    vi.stubGlobal('fetch', vi.fn(() => Promise.resolve(jsonResponse(null, 403))))

    await expect(fetchPowerPoints()).rejects.toBeInstanceOf(ApiError)
  })
})

describe('createPowerPoint', () => {
  afterEach(() => {
    vi.unstubAllGlobals()
  })

  it('posts the Room id and name and returns the created Power Point', async () => {
    const fetchMock = vi.fn((_url: string, _init?: RequestInit) =>
      Promise.resolve(jsonResponse({ id: 'pp-1', roomId: 'room-1', name: 'Office Desk', archivedAt: null })),
    )
    vi.stubGlobal('fetch', fetchMock)

    const result = await createPowerPoint('room-1', 'Office Desk')

    expect(result).toEqual({ id: 'pp-1', roomId: 'room-1', name: 'Office Desk', archivedAt: null })
    const [url, init] = fetchMock.mock.calls[0]!
    expect(url).toBe('/api/power-points')
    expect(JSON.parse(init!.body as string)).toEqual({ roomId: 'room-1', name: 'Office Desk' })
  })

  it('throws an ApiError on a non-2xx response (e.g. duplicate name)', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn(() => Promise.resolve(jsonResponse({ detail: 'A Power Point named \'Office Desk\' already exists in this Room.' }, 400))),
    )

    await expect(createPowerPoint('room-1', 'Office Desk')).rejects.toBeInstanceOf(ApiError)
  })
})
