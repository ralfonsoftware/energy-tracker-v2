import { afterEach, describe, expect, it, vi } from 'vitest'
import { ApiError, fetchMeterReadingHistory, updateMeterReading } from './meter-reading-history-api'

function jsonResponse(body: object | null, status = 200) {
  return new Response(body === null ? null : JSON.stringify(body), { status })
}

describe('fetchMeterReadingHistory', () => {
  afterEach(() => {
    vi.unstubAllGlobals()
  })

  it('returns the parsed page on a 200 response', async () => {
    const page = {
      items: [
        {
          id: '11111111-1111-1111-1111-111111111111',
          kwhValue: 4821.5,
          readingTimestamp: '2026-08-15T14:32:00+00:00',
          version: 0,
          isPendingRegression: false,
          correctedFromKwhValue: null,
          correctedAtUtc: null,
        },
      ],
      totalCount: 1,
      page: 1,
      pageSize: 20,
    }
    const fetchMock = vi.fn(() => Promise.resolve(jsonResponse(page)))
    vi.stubGlobal('fetch', fetchMock)

    const result = await fetchMeterReadingHistory(1, 20)

    expect(result).toEqual(page)
    expect(fetchMock).toHaveBeenCalledWith('/api/meter-readings?page=1&pageSize=20', { credentials: 'include' })
  })

  it('throws an ApiError on a non-2xx response', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn(() => Promise.resolve(new Response(JSON.stringify({ detail: 'No Household' }), { status: 403 }))),
    )

    await expect(fetchMeterReadingHistory(1, 20)).rejects.toBeInstanceOf(ApiError)
    await expect(fetchMeterReadingHistory(1, 20)).rejects.toMatchObject({ status: 403, detail: 'No Household' })
  })
})

describe('updateMeterReading', () => {
  afterEach(() => {
    vi.unstubAllGlobals()
  })

  it('PUTs the id/kwhValue/version and returns the parsed item on a 200 response', async () => {
    const updated = {
      id: '11111111-1111-1111-1111-111111111111',
      kwhValue: 4900,
      readingTimestamp: '2026-08-15T14:32:00+00:00',
      version: 1,
    }
    const fetchMock = vi.fn(() => Promise.resolve(jsonResponse(updated)))
    vi.stubGlobal('fetch', fetchMock)

    const result = await updateMeterReading('11111111-1111-1111-1111-111111111111', 4900, 0)

    expect(result).toEqual(updated)
    expect(fetchMock).toHaveBeenCalledWith('/api/meter-readings/11111111-1111-1111-1111-111111111111', {
      method: 'PUT',
      credentials: 'include',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ kwhValue: 4900, version: 0 }),
    })
  })

  it('throws an ApiError on a non-2xx response', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn(() => Promise.resolve(new Response(JSON.stringify({ detail: 'Conflict' }), { status: 409 }))),
    )

    await expect(updateMeterReading('11111111-1111-1111-1111-111111111111', 4900, 0)).rejects.toBeInstanceOf(ApiError)
    await expect(updateMeterReading('11111111-1111-1111-1111-111111111111', 4900, 0)).rejects.toMatchObject({
      status: 409,
      detail: 'Conflict',
    })
  })
})
