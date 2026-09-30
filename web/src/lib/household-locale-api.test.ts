import { afterEach, describe, expect, it, vi } from 'vitest'
import { updateHouseholdLocale } from './household-locale-api'

describe('updateHouseholdLocale', () => {
  afterEach(() => vi.unstubAllGlobals())

  it('PUTs the locale as JSON with credentials', async () => {
    const fetchMock = vi.fn().mockResolvedValue(new Response('{}', { status: 200 }))
    vi.stubGlobal('fetch', fetchMock)

    await updateHouseholdLocale('h-1', 'en-US')

    expect(fetchMock).toHaveBeenCalledWith('/api/households/h-1/locale', {
      method: 'PUT',
      credentials: 'include',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ locale: 'en-US' }),
    })
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
