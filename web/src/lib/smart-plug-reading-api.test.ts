import { afterEach, describe, expect, it, vi } from 'vitest'
import { ApiError, fetchPerPlugMeasuredData } from './smart-plug-reading-api'

function jsonResponse(body: object | null, status = 200) {
  return new Response(body === null ? null : JSON.stringify(body), { status })
}

describe('fetchPerPlugMeasuredData', () => {
  afterEach(() => {
    vi.unstubAllGlobals()
  })

  it('returns an empty array on a 200 response with an empty JSON array', async () => {
    vi.stubGlobal('fetch', vi.fn(() => Promise.resolve(jsonResponse([]))))

    const result = await fetchPerPlugMeasuredData()

    expect(result).toEqual([])
  })

  it('returns the parsed RoomMeasuredDataDto array on a 200 response with a JSON body', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn(() =>
        Promise.resolve(
          jsonResponse([
            {
              roomName: 'Living Room',
              totalKwh: 60,
              powerPoints: [
                {
                  powerPointName: 'TV Power Point',
                  totalKwh: 60,
                  devices: [
                    { deviceName: 'Smart TV', totalKwh: 38 },
                    { deviceName: 'Games Console', totalKwh: 22 },
                  ],
                },
              ],
            },
          ]),
        ),
      ),
    )

    const result = await fetchPerPlugMeasuredData()

    expect(result).toEqual([
      {
        roomName: 'Living Room',
        totalKwh: 60,
        powerPoints: [
          {
            powerPointName: 'TV Power Point',
            totalKwh: 60,
            devices: [
              { deviceName: 'Smart TV', totalKwh: 38 },
              { deviceName: 'Games Console', totalKwh: 22 },
            ],
          },
        ],
      },
    ])
  })

  it('throws an ApiError on a non-2xx response', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn(() => Promise.resolve(new Response(JSON.stringify({ detail: 'No Household' }), { status: 403 }))),
    )

    await expect(fetchPerPlugMeasuredData()).rejects.toBeInstanceOf(ApiError)
    await expect(fetchPerPlugMeasuredData()).rejects.toMatchObject({ status: 403, detail: 'No Household' })
  })
})
