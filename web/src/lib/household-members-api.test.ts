import { afterEach, describe, expect, it, vi } from 'vitest'
import { ApiError } from '@/lib/api-client'
import {
  fetchHouseholdMembers,
  fetchOpenInvites,
  removeHouseholdMember,
  revokeInvite,
} from './household-members-api'

function stubFetch(response: Response) {
  const fetchMock = vi.fn().mockResolvedValue(response)
  vi.stubGlobal('fetch', fetchMock)
  return fetchMock
}

function problem(status: number, errorCode: string) {
  return new Response(JSON.stringify({ detail: 'nope', errorCode }), {
    status,
    headers: { 'Content-Type': 'application/problem+json' },
  })
}

describe('household-members-api', () => {
  afterEach(() => vi.unstubAllGlobals())

  it('fetchHouseholdMembers GETs the list with credentials and unwraps members', async () => {
    const members = [{ id: 'm-1', displayName: 'Alex', isCurrentMember: true, joinedAtUtc: '2026-10-01T08:00:00Z' }]
    const fetchMock = stubFetch(new Response(JSON.stringify({ members }), { status: 200 }))

    await expect(fetchHouseholdMembers()).resolves.toEqual(members)

    const [url, init] = fetchMock.mock.calls[0]!
    expect(url).toBe('/api/household-members')
    expect(init.method ?? 'GET').toBe('GET')
    expect(init.credentials).toBe('include')
  })

  it('fetchOpenInvites GETs the open invites and unwraps invites', async () => {
    const invites = [{ id: 'i-1', createdAtUtc: '2026-10-10T08:00:00Z', expiresAtUtc: '2026-10-17T08:00:00Z' }]
    const fetchMock = stubFetch(new Response(JSON.stringify({ invites }), { status: 200 }))

    await expect(fetchOpenInvites()).resolves.toEqual(invites)

    expect(fetchMock.mock.calls[0]![0]).toBe('/api/household-invites')
  })

  it('passes an abort signal through to fetch', async () => {
    const fetchMock = stubFetch(new Response(JSON.stringify({ members: [] }), { status: 200 }))
    const controller = new AbortController()

    await fetchHouseholdMembers(controller.signal)

    expect(fetchMock.mock.calls[0]![1].signal).toBe(controller.signal)
  })

  it('removeHouseholdMember DELETEs the id with the CSRF header and does not parse the 204', async () => {
    const fetchMock = stubFetch(new Response(null, { status: 204 }))

    await expect(removeHouseholdMember('m-2')).resolves.toBeUndefined()

    const [url, init] = fetchMock.mock.calls[0]!
    expect(url).toBe('/api/household-members/m-2')
    expect(init).toMatchObject({ method: 'DELETE', credentials: 'include' })
    expect(new Headers(init.headers).get('X-Requested-With')).toBe('fetch')
  })

  it('revokeInvite DELETEs the invite id with the CSRF header', async () => {
    const fetchMock = stubFetch(new Response(null, { status: 204 }))

    await revokeInvite('i-9')

    const [url, init] = fetchMock.mock.calls[0]!
    expect(url).toBe('/api/household-invites/i-9')
    expect(init).toMatchObject({ method: 'DELETE' })
    expect(new Headers(init.headers).get('X-Requested-With')).toBe('fetch')
  })

  it.each(['last-member', 'members-changed', 'member-not-found'])('keeps the errorCode %s on the thrown ApiError', async (code) => {
    stubFetch(problem(code === 'member-not-found' ? 404 : 409, code))

    const error = await removeHouseholdMember('m-2').catch((e: unknown) => e)

    expect(error).toBeInstanceOf(ApiError)
    expect((error as ApiError).errorCode).toBe(code)
  })

  it('revokeInvite surfaces a 404 as an ApiError with invite-not-found', async () => {
    stubFetch(problem(404, 'invite-not-found'))

    const error = await revokeInvite('i-9').catch((e: unknown) => e)

    expect((error as ApiError).status).toBe(404)
    expect((error as ApiError).errorCode).toBe('invite-not-found')
  })

  it('a failed list request throws an ApiError', async () => {
    stubFetch(new Response(null, { status: 403 }))

    await expect(fetchHouseholdMembers()).rejects.toBeInstanceOf(ApiError)
    await expect(fetchOpenInvites()).rejects.toBeInstanceOf(ApiError)
  })

  it.each([
    ['an empty array', '[]'],
    ['null', 'null'],
    ['an object without the list', '{}'],
  ])('a 200 with %s is a failed load, not an empty household', async (_label, body) => {
    stubFetch(new Response(body, { status: 200 }))
    await expect(fetchHouseholdMembers()).rejects.toThrow()

    stubFetch(new Response(body, { status: 200 }))
    await expect(fetchOpenInvites()).rejects.toThrow()
  })
})
