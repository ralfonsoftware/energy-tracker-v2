import { afterEach, describe, expect, it, vi } from 'vitest'
import { ApiError, fetchTariffCheckReminder } from './tariff-check-api'

function jsonResponse(body: object | null, status = 200) {
  return new Response(body === null ? null : JSON.stringify(body), { status })
}

describe('fetchTariffCheckReminder', () => {
  afterEach(() => {
    vi.unstubAllGlobals()
  })

  it('returns null on a 200 response with an empty body', async () => {
    vi.stubGlobal('fetch', vi.fn(() => Promise.resolve(jsonResponse(null))))

    const result = await fetchTariffCheckReminder()

    expect(result).toBeNull()
  })

  it('returns the parsed TariffCheckReminderDto on a 200 response with a JSON body', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn(() =>
        Promise.resolve(
          jsonResponse({
            isDue: true,
            gateOpensAtUtc: '2026-06-01T00:00:00+00:00',
          }),
        ),
      ),
    )

    const result = await fetchTariffCheckReminder()

    expect(result).toEqual({
      isDue: true,
      gateOpensAtUtc: '2026-06-01T00:00:00+00:00',
    })
  })

  it('throws an ApiError on a non-2xx response', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn(() => Promise.resolve(new Response(JSON.stringify({ detail: 'No Household' }), { status: 403 }))),
    )

    await expect(fetchTariffCheckReminder()).rejects.toBeInstanceOf(ApiError)
    await expect(fetchTariffCheckReminder()).rejects.toMatchObject({ status: 403, detail: 'No Household' })
  })
})
