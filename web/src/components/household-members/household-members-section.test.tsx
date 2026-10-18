import 'fake-indexeddb/auto'
import { render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { IDBFactory } from 'fake-indexeddb'
import { HouseholdMembersSection } from './household-members-section'
import { enqueue, listPending } from '@/lib/offline-queue'
import { checkPendingReadingsBeforeLogoff } from '@/lib/logoff'
import { REQUESTED_WITH_HEADER, REQUESTED_WITH_VALUE } from '@/lib/api-client'
import type { HouseholdMember, OpenInvite } from '@/lib/household-members-api'

vi.mock('@/lib/logoff', () => ({ checkPendingReadingsBeforeLogoff: vi.fn() }))

const householdId = '11111111-1111-1111-1111-111111111111'

const ME: HouseholdMember = { id: 'm-me', displayName: 'Ralf', isCurrentMember: true, joinedAtUtc: '2026-08-12T12:00:00Z' }
const MIRA: HouseholdMember = { id: 'm-mira', displayName: 'Mira', isCurrentMember: false, joinedAtUtc: '2026-09-03T12:00:00Z' }
const TOBIAS: HouseholdMember = { id: 'm-tobias', displayName: 'Tobias', isCurrentMember: false, joinedAtUtc: '2026-10-01T12:00:00Z' }
const INVITE_1: OpenInvite = { id: 'i-1', createdAtUtc: '2026-10-14T12:00:00Z', expiresAtUtc: '2026-10-21T12:00:00Z' }
const INVITE_2: OpenInvite = { id: 'i-2', createdAtUtc: '2026-10-17T12:00:00Z', expiresAtUtc: '2026-10-24T12:00:00Z' }

function json(body: unknown, status = 200) {
  return new Response(body === null ? null : JSON.stringify(body), { status })
}

function problem(status: number, errorCode: string) {
  return json({ status, errorCode, detail: 'x' }, status)
}

interface Backend {
  members: HouseholdMember[]
  invites: OpenInvite[]
  // Answers a DELETE; the default succeeds and applies the removal to the lists so the refetch shows it.
  onDeleteMember?: (id: string) => Promise<Response> | Response
  onDeleteInvite?: (id: string) => Promise<Response> | Response
  onCreateInvite?: () => Response
  memberListStatus?: number
}

function stubBackend(backend: Backend) {
  const calls: { method: string; url: string; headers: Headers; signal?: AbortSignal | null }[] = []
  const fetchMock = vi.fn(async (input: string | URL | Request, init?: RequestInit) => {
    const url = String(input)
    const method = init?.method ?? 'GET'
    calls.push({ method, url, headers: new Headers(init?.headers), signal: init?.signal })

    if (method === 'GET' && url === '/api/household-members') {
      return backend.memberListStatus && backend.memberListStatus !== 200
        ? problem(backend.memberListStatus, 'boom')
        : json({ members: backend.members })
    }
    if (method === 'GET' && url === '/api/household-invites') {
      return json({ invites: backend.invites })
    }
    if (method === 'DELETE' && url.startsWith('/api/household-members/')) {
      const id = url.split('/').pop()!
      if (backend.onDeleteMember) {
        return backend.onDeleteMember(id)
      }
      backend.members = backend.members.filter((m) => m.id !== id)
      return json(null, 204)
    }
    if (method === 'DELETE' && url.startsWith('/api/household-invites/')) {
      const id = url.split('/').pop()!
      if (backend.onDeleteInvite) {
        return backend.onDeleteInvite(id)
      }
      backend.invites = backend.invites.filter((i) => i.id !== id)
      return json(null, 204)
    }
    if (method === 'POST' && url === '/api/household-invites') {
      return backend.onCreateInvite?.() ?? json({ token: 'tok-new', expiresAtUtc: '2026-10-25T12:00:00Z' })
    }
    return json(null)
  })
  vi.stubGlobal('fetch', fetchMock)
  return { fetchMock, calls }
}

function stubLocation() {
  const originalLocation = window.location
  const assign = vi.fn()
  Object.defineProperty(window, 'location', { value: { ...originalLocation, assign }, writable: true })
  return { assign, restore: () => Object.defineProperty(window, 'location', { value: originalLocation, writable: true }) }
}

async function renderLoaded() {
  render(<HouseholdMembersSection householdId={householdId} />)
  await screen.findByRole('heading', { name: 'Household' })
  await waitFor(() => expect(screen.queryByText('Loading members and invites…')).not.toBeInTheDocument())
}

describe('HouseholdMembersSection', () => {
  beforeEach(() => {
    globalThis.indexedDB = new IDBFactory()
    vi.mocked(checkPendingReadingsBeforeLogoff).mockResolvedValue({ pendingCount: 0 })
  })

  afterEach(() => {
    vi.unstubAllGlobals()
    vi.clearAllMocks()
  })

  describe('lists (AC #1, #14, #16)', () => {
    it('renders the members and the open invites as lists, marks the caller and offers Remove only on the others', async () => {
      stubBackend({ members: [ME, MIRA, TOBIAS], invites: [INVITE_1, INVITE_2] })

      await renderLoaded()

      const [memberList, inviteList] = screen.getAllByRole('list')
      expect(within(memberList).getAllByRole('listitem')).toHaveLength(3)
      expect(within(inviteList).getAllByRole('listitem')).toHaveLength(2)

      const own = within(memberList).getAllByRole('listitem')[0]
      expect(within(own).getByText('Ralf')).toBeInTheDocument()
      expect(within(own).getByText('You')).toBeInTheDocument()
      expect(within(own).getByRole('button', { name: 'Leave household' })).toBeInTheDocument()
      expect(within(own).queryByRole('button', { name: /^Remove/ })).not.toBeInTheDocument()

      expect(screen.getByRole('button', { name: 'Remove Mira' })).toBeInTheDocument()
      expect(screen.getByRole('button', { name: 'Remove Tobias' })).toBeInTheDocument()
      expect(screen.queryByRole('button', { name: 'Remove Ralf' })).not.toBeInTheDocument()

      expect(screen.getByText('Joined Sep 3, 2026')).toBeInTheDocument()
      expect(screen.getByText('Created Oct 14, 2026 · expires Oct 21, 2026')).toBeInTheDocument()
      expect(screen.getByRole('button', { name: 'Revoke invite created Oct 14, 2026' })).toBeInTheDocument()
    })

    it('never renders a token, a link or a hash in the invites list', async () => {
      stubBackend({ members: [ME, MIRA], invites: [INVITE_1] })

      await renderLoaded()

      expect(screen.queryByText(/\/join\//)).not.toBeInTheDocument()
      expect(screen.queryByLabelText('Invite link')).not.toBeInTheDocument()
    })

    it('shows the generic label for a member without a display name, in the list, the aria-label and the dialog (AC #14)', async () => {
      const user = userEvent.setup()
      stubBackend({ members: [ME, { ...MIRA, id: 'm-none', displayName: null }], invites: [] })

      await renderLoaded()

      expect(screen.getByText('Household member')).toBeInTheDocument()
      await user.click(screen.getByRole('button', { name: 'Remove Household member' }))
      expect(await screen.findByRole('dialog', { name: 'Remove Household member?' })).toBeInTheDocument()
    })

    it('shows a quiet empty state when there are no open invites', async () => {
      stubBackend({ members: [ME, MIRA], invites: [] })

      await renderLoaded()

      expect(screen.getByText(/No open invites/)).toBeInTheDocument()
    })

    it('shows a load error with a retry that reloads', async () => {
      const user = userEvent.setup()
      const backend: Backend = { members: [ME, MIRA], invites: [], memberListStatus: 500 }
      stubBackend(backend)

      render(<HouseholdMembersSection householdId={householdId} />)

      expect(await screen.findByText("Couldn't load members and invites.")).toBeInTheDocument()
      backend.memberListStatus = 200
      await user.click(screen.getByRole('button', { name: 'Try again' }))

      expect(await screen.findByRole('button', { name: 'Remove Mira' })).toBeInTheDocument()
    })

    it('aborts its requests when it unmounts (no state update after unmount)', async () => {
      const { calls } = stubBackend({ members: [ME], invites: [] })

      const { unmount } = render(<HouseholdMembersSection householdId={householdId} />)
      unmount()

      const gets = calls.filter((c) => c.method === 'GET')
      expect(gets.length).toBeGreaterThan(0)
      expect(gets.every((c) => c.signal?.aborted)).toBe(true)
    })
  })

  describe('sole member (AC #15)', () => {
    it('has no Remove buttons and a Leave that is aria-disabled with its visible reason, and clicking it opens nothing', async () => {
      const user = userEvent.setup()
      stubBackend({ members: [ME], invites: [INVITE_1] })

      await renderLoaded()

      expect(screen.queryByRole('button', { name: /^Remove/ })).not.toBeInTheDocument()
      const leave = screen.getByRole('button', { name: 'Leave household' })
      expect(leave).toHaveAttribute('aria-disabled', 'true')
      const reason = screen.getByText("You're the only member. Invite someone before you can leave.")
      expect(leave.getAttribute('aria-describedby')).toBe(reason.closest('[id]')?.id)

      await user.click(leave)
      expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
    })
  })

  describe('remove (AC #2, #14, #15, #16)', () => {
    it('opens a confirm dialog that names the person and says what happens, and sends nothing until confirmed', async () => {
      const user = userEvent.setup()
      const { calls } = stubBackend({ members: [ME, MIRA], invites: [INVITE_1] })

      await renderLoaded()
      await user.click(screen.getByRole('button', { name: 'Remove Mira' }))

      const dialog = await screen.findByRole('dialog', { name: 'Remove Mira?' })
      expect(within(dialog).getByText(/loses access to this household right away/)).toBeInTheDocument()
      expect(within(dialog).getByText(/Every open invite link stops working/)).toBeInTheDocument()
      expect(within(dialog).getByText(/data stays/)).toBeInTheDocument()
      expect(within(dialog).getByText('Member since Sep 3, 2026')).toBeInTheDocument()
      expect(calls.some((c) => c.method === 'DELETE')).toBe(false)
    })

    it('cancel closes the dialog without a request', async () => {
      const user = userEvent.setup()
      const { calls } = stubBackend({ members: [ME, MIRA], invites: [] })

      await renderLoaded()
      await user.click(screen.getByRole('button', { name: 'Remove Mira' }))
      await user.click(within(await screen.findByRole('dialog')).getByRole('button', { name: 'Cancel' }))

      await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument())
      expect(calls.some((c) => c.method === 'DELETE')).toBe(false)
    })

    it('confirm sends DELETE with the id of the clicked row and the CSRF header, reloads, announces it and moves focus to the heading', async () => {
      const user = userEvent.setup()
      const twinA: HouseholdMember = { id: 'm-alex-a', displayName: 'Alex', isCurrentMember: false, joinedAtUtc: '2026-09-03T12:00:00Z' }
      const twinB: HouseholdMember = { id: 'm-alex-b', displayName: 'Alex', isCurrentMember: false, joinedAtUtc: '2026-10-09T12:00:00Z' }
      const { calls } = stubBackend({ members: [ME, twinA, twinB], invites: [] })

      await renderLoaded()
      const [, secondAlex] = screen.getAllByRole('button', { name: 'Remove Alex' })
      await user.click(secondAlex)
      const dialog = await screen.findByRole('dialog', { name: 'Remove Alex?' })
      expect(within(dialog).getByText('Member since Oct 9, 2026')).toBeInTheDocument()
      await user.click(within(dialog).getByRole('button', { name: 'Remove' }))

      await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument())
      const del = calls.find((c) => c.method === 'DELETE')!
      expect(del.url).toBe('/api/household-members/m-alex-b')
      expect(del.headers.get(REQUESTED_WITH_HEADER)).toBe(REQUESTED_WITH_VALUE)
      expect(screen.getAllByRole('button', { name: 'Remove Alex' })).toHaveLength(1)
      expect(screen.getByRole('status')).toHaveTextContent('Alex was removed.')
      expect(screen.getByRole('heading', { name: 'Household' })).toHaveFocus()
    })

    it('disables every button while the request is in flight', async () => {
      const user = userEvent.setup()
      let release!: (r: Response) => void
      stubBackend({
        members: [ME, MIRA, TOBIAS],
        invites: [INVITE_1],
        onDeleteMember: () => new Promise<Response>((resolve) => (release = resolve)),
      })

      await renderLoaded()
      await user.click(screen.getByRole('button', { name: 'Remove Mira' }))
      const dialog = await screen.findByRole('dialog')
      await user.click(within(dialog).getByRole('button', { name: 'Remove' }))

      expect(await within(dialog).findByRole('button', { name: 'Removing…' })).toBeDisabled()
      expect(within(dialog).getByRole('button', { name: 'Cancel' })).toBeDisabled()
      release(json(null, 204))
      await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument())
    })

    it('keeps the dialog open with an alert on a generic failure and re-enables the buttons', async () => {
      const user = userEvent.setup()
      stubBackend({ members: [ME, MIRA], invites: [], onDeleteMember: () => json({ detail: 'x' }, 500) })

      await renderLoaded()
      await user.click(screen.getByRole('button', { name: 'Remove Mira' }))
      const dialog = await screen.findByRole('dialog')
      await user.click(within(dialog).getByRole('button', { name: 'Remove' }))

      expect(await within(dialog).findByRole('alert')).toHaveTextContent("Couldn't remove Mira. Check your connection and try again.")
      expect(within(dialog).getByRole('button', { name: 'Remove' })).toBeEnabled()
      expect(within(dialog).getByRole('button', { name: 'Cancel' })).toBeEnabled()
    })

    it.each(['members-changed', 'member-not-found'])('a 409/404 %s closes the dialog, reloads the list and says so', async (code) => {
      const user = userEvent.setup()
      const backend: Backend = {
        members: [ME, MIRA, TOBIAS],
        invites: [],
        onDeleteMember: () => {
          // Someone else removed Tobias in the meantime; the refetch shows it.
          backend.members = [ME, MIRA]
          return problem(code === 'members-changed' ? 409 : 404, code)
        },
      }
      stubBackend(backend)

      await renderLoaded()
      await user.click(screen.getByRole('button', { name: 'Remove Mira' }))
      await user.click(within(await screen.findByRole('dialog')).getByRole('button', { name: 'Remove' }))

      await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument())
      expect(screen.getByRole('status')).toHaveTextContent("The member list changed in the meantime. We've refreshed it.")
      expect(screen.queryByRole('button', { name: 'Remove Tobias' })).not.toBeInTheDocument()
    })
  })

  describe('leave (AC #13, #15)', () => {
    it('plain variant: no unsynced readings, says what happens and offers "Leave"', async () => {
      const user = userEvent.setup()
      stubBackend({ members: [ME, MIRA], invites: [] })

      await renderLoaded()
      await user.click(screen.getByRole('button', { name: 'Leave household' }))

      const dialog = await screen.findByRole('dialog', { name: 'Leave this household?' })
      expect(within(dialog).getByText(/lose access to this household's data/)).toBeInTheDocument()
      expect(within(dialog).getByText(/Every open invite link stops working/)).toBeInTheDocument()
      expect(await within(dialog).findByRole('button', { name: 'Leave' })).toBeEnabled()
      expect(within(dialog).queryByText('Unsynced readings')).not.toBeInTheDocument()
      expect(checkPendingReadingsBeforeLogoff).toHaveBeenCalledWith(householdId)
    })

    it('the confirm button reads "Checking…" and is disabled until the pending-readings check returns', async () => {
      const user = userEvent.setup()
      let resolveCheck!: (v: { pendingCount: number }) => void
      vi.mocked(checkPendingReadingsBeforeLogoff).mockReturnValue(new Promise((resolve) => (resolveCheck = resolve)))
      stubBackend({ members: [ME, MIRA], invites: [] })

      await renderLoaded()
      await user.click(screen.getByRole('button', { name: 'Leave household' }))

      const dialog = await screen.findByRole('dialog')
      expect(within(dialog).getByRole('button', { name: 'Checking…' })).toBeDisabled()
      resolveCheck({ pendingCount: 0 })
      expect(await within(dialog).findByRole('button', { name: 'Leave' })).toBeEnabled()
    })

    it('unsynced variant: warns the readings will be deleted from this device and the confirm says so (plural and singular)', async () => {
      const user = userEvent.setup()
      vi.mocked(checkPendingReadingsBeforeLogoff).mockResolvedValue({ pendingCount: 3 })
      stubBackend({ members: [ME, MIRA], invites: [] })

      await renderLoaded()
      await user.click(screen.getByRole('button', { name: 'Leave household' }))

      const dialog = await screen.findByRole('dialog')
      expect(await within(dialog).findByText('Unsynced readings')).toBeInTheDocument()
      expect(within(dialog).getByText("3 Meter Readings couldn't be synced. If you leave, they will be deleted from this device.")).toBeInTheDocument()
      expect(within(dialog).getByRole('button', { name: 'Leave and delete readings' })).toBeEnabled()
    })

    it('unsynced variant: singular copy for exactly one reading', async () => {
      const user = userEvent.setup()
      vi.mocked(checkPendingReadingsBeforeLogoff).mockResolvedValue({ pendingCount: 1 })
      stubBackend({ members: [ME, MIRA], invites: [] })

      await renderLoaded()
      await user.click(screen.getByRole('button', { name: 'Leave household' }))

      expect(
        await screen.findByText("1 Meter Reading couldn't be synced. If you leave, it will be deleted from this device."),
      ).toBeInTheDocument()
    })

    it('confirm sends DELETE with the caller\'s own member id, then removes only this household\'s queued readings and navigates to /', async () => {
      const user = userEvent.setup()
      const loc = stubLocation()
      vi.mocked(checkPendingReadingsBeforeLogoff).mockResolvedValue({ pendingCount: 2 })
      await enqueue({ householdId, kwhValue: 1, readingTimestamp: '2026-10-01T00:00:00Z', idempotencyKey: 'mine-1' })
      await enqueue({ householdId, kwhValue: 2, readingTimestamp: '2026-10-02T00:00:00Z', idempotencyKey: 'mine-2' })
      await enqueue({ householdId: 'other-household', kwhValue: 3, readingTimestamp: '2026-10-03T00:00:00Z', idempotencyKey: 'theirs-1' })
      const { calls } = stubBackend({ members: [ME, MIRA], invites: [] })

      try {
        await renderLoaded()
        await user.click(screen.getByRole('button', { name: 'Leave household' }))
        await user.click(await screen.findByRole('button', { name: 'Leave and delete readings' }))

        await waitFor(() => expect(loc.assign).toHaveBeenCalledWith('/'))
        const del = calls.find((c) => c.method === 'DELETE')!
        expect(del.url).toBe('/api/household-members/m-me')
        expect(del.headers.get(REQUESTED_WITH_HEADER)).toBe(REQUESTED_WITH_VALUE)
        expect((await listPending()).map((r) => r.idempotencyKey)).toEqual(['theirs-1'])
      } finally {
        loc.restore()
      }
    })

    it('a failed leave deletes nothing, shows an alert and does not navigate', async () => {
      const user = userEvent.setup()
      const loc = stubLocation()
      vi.mocked(checkPendingReadingsBeforeLogoff).mockResolvedValue({ pendingCount: 1 })
      await enqueue({ householdId, kwhValue: 1, readingTimestamp: '2026-10-01T00:00:00Z', idempotencyKey: 'mine-1' })
      stubBackend({ members: [ME, MIRA], invites: [], onDeleteMember: () => json({ detail: 'x' }, 500) })

      try {
        await renderLoaded()
        await user.click(screen.getByRole('button', { name: 'Leave household' }))
        await user.click(await screen.findByRole('button', { name: 'Leave and delete readings' }))

        expect(await screen.findByRole('alert')).toHaveTextContent("Couldn't leave the household. Check your connection and try again.")
        expect(loc.assign).not.toHaveBeenCalled()
        expect((await listPending()).map((r) => r.idempotencyKey)).toEqual(['mine-1'])
        expect(screen.getByRole('button', { name: 'Leave and delete readings' })).toBeEnabled()
      } finally {
        loc.restore()
      }
    })

    it('409 last-member shows its own copy, keeps the confirm disabled and refreshes the list behind the dialog', async () => {
      const user = userEvent.setup()
      const loc = stubLocation()
      const backend: Backend = {
        members: [ME, MIRA],
        invites: [],
        onDeleteMember: () => {
          backend.members = [ME]
          return problem(409, 'last-member')
        },
      }
      stubBackend(backend)

      try {
        await renderLoaded()
        await user.click(screen.getByRole('button', { name: 'Leave household' }))
        await user.click(await screen.findByRole('button', { name: 'Leave' }))

        expect(await screen.findByRole('alert')).toHaveTextContent("You're the only member left, so you can't leave. Invite someone first.")
        expect(within(screen.getByRole('dialog')).getByRole('button', { name: 'Leave' })).toBeDisabled()
        expect(loc.assign).not.toHaveBeenCalled()
        await waitFor(() => expect(screen.queryByRole('button', { name: /^Remove/ })).not.toBeInTheDocument())
      } finally {
        loc.restore()
      }
    })

    it('still leaves and navigates when the pending-readings check itself fails (IndexedDB unavailable)', async () => {
      const user = userEvent.setup()
      const loc = stubLocation()
      vi.mocked(checkPendingReadingsBeforeLogoff).mockRejectedValue(new Error('idb'))
      stubBackend({ members: [ME, MIRA], invites: [] })

      try {
        await renderLoaded()
        await user.click(screen.getByRole('button', { name: 'Leave household' }))
        await user.click(await screen.findByRole('button', { name: 'Leave' }))

        await waitFor(() => expect(loc.assign).toHaveBeenCalledWith('/'))
      } finally {
        loc.restore()
      }
    })
  })

  describe('revoke (AC #9, #16)', () => {
    it('revokes immediately (no dialog) with the clicked invite\'s id, drops the row, announces it and moves focus to the heading', async () => {
      const user = userEvent.setup()
      const { calls } = stubBackend({ members: [ME, MIRA], invites: [INVITE_1, INVITE_2] })

      await renderLoaded()
      await user.click(screen.getByRole('button', { name: 'Revoke invite created Oct 17, 2026' }))

      await waitFor(() => expect(screen.queryByRole('button', { name: 'Revoke invite created Oct 17, 2026' })).not.toBeInTheDocument())
      const del = calls.find((c) => c.method === 'DELETE')!
      expect(del.url).toBe('/api/household-invites/i-2')
      expect(del.headers.get(REQUESTED_WITH_HEADER)).toBe(REQUESTED_WITH_VALUE)
      expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
      expect(screen.getByRole('status')).toHaveTextContent('Invite revoked.')
      expect(screen.getByRole('heading', { name: 'Household' })).toHaveFocus()
      expect(screen.getByRole('button', { name: 'Revoke invite created Oct 14, 2026' })).toBeInTheDocument()
    })

    it('a 404 invite-not-found means "already gone": reloads the list and says so', async () => {
      const user = userEvent.setup()
      const backend: Backend = {
        members: [ME, MIRA],
        invites: [INVITE_1, INVITE_2],
        onDeleteInvite: () => {
          backend.invites = [INVITE_2]
          return problem(404, 'invite-not-found')
        },
      }
      stubBackend(backend)

      await renderLoaded()
      await user.click(screen.getByRole('button', { name: 'Revoke invite created Oct 14, 2026' }))

      expect(await screen.findByText("That invite was already gone. We've refreshed the list.")).toBeInTheDocument()
      expect(screen.queryByRole('button', { name: 'Revoke invite created Oct 14, 2026' })).not.toBeInTheDocument()
    })

    it('a failed revoke shows an alert in the invites group and keeps the row', async () => {
      const user = userEvent.setup()
      stubBackend({ members: [ME, MIRA], invites: [INVITE_1], onDeleteInvite: () => json({ detail: 'x' }, 500) })

      await renderLoaded()
      await user.click(screen.getByRole('button', { name: 'Revoke invite created Oct 14, 2026' }))

      expect(await screen.findByRole('alert')).toHaveTextContent("Couldn't revoke the invite. Check your connection and try again.")
      expect(screen.getByRole('button', { name: 'Revoke invite created Oct 14, 2026' })).toBeEnabled()
    })
  })

  describe('invite creation (AC #1)', () => {
    it('a newly created invite appears in the open list without a reload, and is announced', async () => {
      const user = userEvent.setup()
      const backend: Backend = {
        members: [ME, MIRA],
        invites: [INVITE_1],
        onCreateInvite: () => {
          backend.invites = [INVITE_2, INVITE_1]
          return json({ token: 'tok-new', expiresAtUtc: '2026-10-25T12:00:00Z' })
        },
      }
      stubBackend(backend)

      await renderLoaded()
      expect(screen.getAllByRole('button', { name: /^Revoke invite/ })).toHaveLength(1)
      const [narrowGenerate] = screen.getAllByRole('button', { name: 'Invite a member' })
      await user.click(narrowGenerate)

      await waitFor(() => expect(screen.getAllByRole('button', { name: /^Revoke invite/ })).toHaveLength(2))
      expect(screen.getByRole('status')).toHaveTextContent('Invite created.')
    })
  })
})
