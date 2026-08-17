import { afterEach, describe, expect, it, vi } from 'vitest'
import { ApiError, fetchCurrentStatus } from './status-api'

function jsonResponse(body: object | null, status = 200) {
  return new Response(body === null ? null : JSON.stringify(body), { status })
}

describe('fetchCurrentStatus', () => {
  afterEach(() => {
    vi.unstubAllGlobals()
  })

  it('returns null on a 200 response with an empty body', async () => {
    vi.stubGlobal('fetch', vi.fn(() => Promise.resolve(jsonResponse(null))))

    const result = await fetchCurrentStatus()

    expect(result).toBeNull()
  })

  it('returns the parsed StatusDto on a 200 response with a JSON body', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn(() =>
        Promise.resolve(
          jsonResponse({
            status: 'withinRange',
            paceToDateKwh: 1200.5,
            baselineToDateKwh: 1300.25,
            isLowConfidence: false,
          }),
        ),
      ),
    )

    const result = await fetchCurrentStatus()

    expect(result).toEqual({
      status: 'withinRange',
      paceToDateKwh: 1200.5,
      baselineToDateKwh: 1300.25,
      isLowConfidence: false,
    })
  })

  it('throws an ApiError on a non-2xx response', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn(() => Promise.resolve(new Response(JSON.stringify({ detail: 'No Household' }), { status: 403 }))),
    )

    await expect(fetchCurrentStatus()).rejects.toBeInstanceOf(ApiError)
    await expect(fetchCurrentStatus()).rejects.toMatchObject({ status: 403, detail: 'No Household' })
  })
})
