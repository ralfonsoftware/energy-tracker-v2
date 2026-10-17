import { afterEach, describe, expect, it, vi } from 'vitest'
import { updateHouseholdLocale } from './household-locale-api'

describe('updateHouseholdLocale', () => {
  afterEach(() => vi.unstubAllGlobals())

  it('PUTs the locale as JSON with credentials and the CSRF header', async () => {
    const fetchMock = vi.fn().mockResolvedValue(new Response('{}', { status: 200 }))
    vi.stubGlobal('fetch', fetchMock)

    await updateHouseholdLocale('h-1', 'en-US')

    const [url, init] = fetchMock.mock.calls[0]!
    expect(url).toBe('/api/households/h-1/locale')
    expect(init).toMatchObject({ method: 'PUT', credentials: 'include', body: JSON.stringify({ locale: 'en-US' }) })
    const headers = new Headers(init.headers)
    expect(headers.get('Content-Type')).toBe('application/json')
    expect(headers.get('X-Requested-With')).toBe('fetch')
  })

  it('throws on a non-ok response', async () => {
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(new Response(null, { status: 500 })))
    await expect(updateHouseholdLocale('h-1', 'en-US')).rejects.toThrow()
  })

  it('lets network errors propagate', async () => {
    vi.stubGlobal('fetch', vi.fn().mockRejectedValue(new TypeError('offline')))
    await expect(updateHouseholdLocale('h-1', 'en-US')).rejects.toThrow('offline')
  })
})
