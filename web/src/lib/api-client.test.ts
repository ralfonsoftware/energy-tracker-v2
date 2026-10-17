import { afterEach, describe, expect, it, vi } from 'vitest'
import { ApiError, REQUESTED_WITH_HEADER, REQUESTED_WITH_VALUE, apiFetch, toApiError } from '@/lib/api-client'

function stubFetch(response: Response = new Response(null, { status: 200 })) {
  const fetchMock = vi.fn(() => Promise.resolve(response))
  vi.stubGlobal('fetch', fetchMock)
  return fetchMock
}

function sentInit(fetchMock: ReturnType<typeof stubFetch>): RequestInit {
  return (fetchMock.mock.calls[0] as unknown as [string, RequestInit])[1]
}

describe('apiFetch', () => {
  afterEach(() => {
    vi.unstubAllGlobals()
  })

  it.each(['GET', 'POST', 'PUT', 'DELETE'])('sends the CSRF header and credentials on %s', async (method) => {
    const fetchMock = stubFetch()

    await apiFetch('/api/rooms', { method })

    const init = sentInit(fetchMock)
    expect(init.method).toBe(method)
    expect(init.credentials).toBe('include')
    expect(new Headers(init.headers).get(REQUESTED_WITH_HEADER)).toBe(REQUESTED_WITH_VALUE)
  })

  it('names the header X-Requested-With with the value fetch', () => {
    expect(REQUESTED_WITH_HEADER).toBe('X-Requested-With')
    expect(REQUESTED_WITH_VALUE).toBe('fetch')
  })

  it('sends the header when called with no init at all', async () => {
    const fetchMock = stubFetch()

    await apiFetch('/api/status')

    expect(new Headers(sentInit(fetchMock).headers).get('X-Requested-With')).toBe('fetch')
  })

  it("keeps the caller's headers, body and signal", async () => {
    const fetchMock = stubFetch()
    const controller = new AbortController()

    await apiFetch('/api/rooms', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: '{"name":"Kitchen"}',
      signal: controller.signal,
    })

    const init = sentInit(fetchMock)
    const headers = new Headers(init.headers)
    expect(headers.get('Content-Type')).toBe('application/json')
    expect(headers.get('X-Requested-With')).toBe('fetch')
    expect(init.body).toBe('{"name":"Kitchen"}')
    expect(init.signal).toBe(controller.signal)
  })

  it('accepts headers given as a Headers instance or a tuple array', async () => {
    const fetchMock = stubFetch()

    await apiFetch('/a', { headers: new Headers({ 'X-Custom': '1' }) })
    await apiFetch('/b', { headers: [['X-Custom', '2']] })

    const [first, second] = fetchMock.mock.calls.map((call) => new Headers((call as unknown as [string, RequestInit])[1].headers))
    expect(first.get('X-Custom')).toBe('1')
    expect(second.get('X-Custom')).toBe('2')
    expect(first.get('X-Requested-With')).toBe('fetch')
    expect(second.get('X-Requested-With')).toBe('fetch')
  })

  it('does not set Content-Type for a FormData body', async () => {
    const fetchMock = stubFetch()
    const form = new FormData()
    form.append('file', new Blob(['x']), 'x.csv')

    await apiFetch('/api/smart-plug-imports', { method: 'POST', body: form })

    const init = sentInit(fetchMock)
    expect(new Headers(init.headers).has('Content-Type')).toBe(false)
    expect(init.body).toBe(form)
  })

  it('cannot be downgraded by a caller-supplied credentials value', async () => {
    const fetchMock = stubFetch()

    await apiFetch('/api/rooms', { credentials: 'omit' })

    expect(sentInit(fetchMock).credentials).toBe('include')
  })

  it('returns the raw Response for a non-2xx status without throwing', async () => {
    const response = new Response('{"detail":"nope"}', { status: 400 })
    stubFetch(response)

    const result = await apiFetch('/api/rooms', { method: 'POST' })

    expect(result).toBe(response)
    expect(result.status).toBe(400)
  })

  it('lets a network failure propagate as a rejection', async () => {
    vi.stubGlobal('fetch', vi.fn(() => Promise.reject(new TypeError('Failed to fetch'))))

    await expect(apiFetch('/api/rooms')).rejects.toThrow('Failed to fetch')
  })
})

describe('toApiError', () => {
  it('reads detail and errorCode from a ProblemDetails body', async () => {
    const error = await toApiError(
      new Response(JSON.stringify({ detail: 'Name is required.', errorCode: 'name-required' }), { status: 400 }),
    )

    expect(error).toBeInstanceOf(ApiError)
    expect(error.status).toBe(400)
    expect(error.detail).toBe('Name is required.')
    expect(error.errorCode).toBe('name-required')
    expect(error.message).toBe('Request failed with status 400')
  })

  it('yields null detail and errorCode for an empty body', async () => {
    const error = await toApiError(new Response(null, { status: 500 }))

    expect(error.status).toBe(500)
    expect(error.detail).toBeNull()
    expect(error.errorCode).toBeNull()
  })

  it('yields null detail and errorCode for a non-JSON body', async () => {
    const error = await toApiError(new Response('<html>Bad gateway</html>', { status: 502 }))

    expect(error.detail).toBeNull()
    expect(error.errorCode).toBeNull()
  })
})

describe('ApiError', () => {
  it('is an Error carrying status, detail and errorCode', () => {
    const error = new ApiError(409, 'Conflict', 'version-conflict')

    expect(error).toBeInstanceOf(Error)
    expect(error.status).toBe(409)
    expect(error.detail).toBe('Conflict')
    expect(error.errorCode).toBe('version-conflict')
  })

  it('defaults errorCode to null', () => {
    expect(new ApiError(404, null).errorCode).toBeNull()
  })
})
