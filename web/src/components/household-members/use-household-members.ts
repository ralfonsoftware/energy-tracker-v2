import { useCallback, useEffect, useRef, useState } from 'react'
import { ApiError } from '@/lib/api-client'
import {
  fetchHouseholdMembers,
  fetchOpenInvites,
  removeHouseholdMember,
  revokeInvite,
  type HouseholdMember,
  type OpenInvite,
} from '@/lib/household-members-api'

// What the polite live region says after an action. `name` is already the display label (never null).
export type MembersStatus =
  | { kind: 'removed'; name: string }
  | { kind: 'revoked' }
  | { kind: 'created' }
  | { kind: 'membersChanged' }
  | { kind: 'inviteGone' }

// `changed`: the concurrency guard or a 404 fired, the list was reloaded and the status line says so (AC #15).
export type RemoveOutcome = 'removed' | 'changed' | 'last-member' | 'failed'
export type RevokeOutcome = 'revoked' | 'gone' | 'failed'

export interface UseHouseholdMembersResult {
  members: HouseholdMember[] | null
  invites: OpenInvite[] | null
  loadError: boolean
  // True while any removal, leave or revoke request runs, so no second request can start (the endpoints are rate limited).
  busy: boolean
  status: MembersStatus | null
  revokeFailed: boolean
  reload: () => Promise<void>
  removeMember: (member: HouseholdMember, label: string, options?: { leaving?: boolean }) => Promise<RemoveOutcome>
  revoke: (invite: OpenInvite) => Promise<RevokeOutcome>
  notifyInviteCreated: () => Promise<void>
}

function errorCodeOf(error: unknown): string | null {
  return error instanceof ApiError ? error.errorCode : null
}

// Story 11.9 (AD-26): loads the member and open-invite lists and runs the three mutations. Every request is aborted on
// unmount and a superseded reload is aborted too, so no state is set afterwards (the open InviteGeneratePanel
// deferred entry is this exact omission). Stale data stays visible during a refetch.
export function useHouseholdMembers(): UseHouseholdMembersResult {
  const [members, setMembers] = useState<HouseholdMember[] | null>(null)
  const [invites, setInvites] = useState<OpenInvite[] | null>(null)
  const [loadError, setLoadError] = useState(false)
  const [busy, setBusy] = useState(false)
  const [status, setStatus] = useState<MembersStatus | null>(null)
  const [revokeFailed, setRevokeFailed] = useState(false)
  const controller = useRef<AbortController | null>(null)
  const mounted = useRef(true)

  // A promise chain rather than async/await: every state update sits in a callback, so none runs synchronously in
  // the mount effect below.
  const reload = useCallback((): Promise<void> => {
    controller.current?.abort()
    const current = new AbortController()
    controller.current = current
    return Promise.all([fetchHouseholdMembers(current.signal), fetchOpenInvites(current.signal)])
      .then(([loadedMembers, loadedInvites]) => {
        if (current.signal.aborted) return
        setMembers(loadedMembers)
        setInvites(loadedInvites)
        setLoadError(false)
      })
      .catch(() => {
        if (current.signal.aborted) return
        setLoadError(true)
      })
  }, [])

  useEffect(() => {
    mounted.current = true
    void reload()
    return () => {
      mounted.current = false
      controller.current?.abort()
    }
  }, [reload])

  const settle = () => {
    if (mounted.current) setBusy(false)
  }

  const removeMember = useCallback(
    async (member: HouseholdMember, label: string, options?: { leaving?: boolean }): Promise<RemoveOutcome> => {
      setBusy(true)
      setStatus(null)
      try {
        await removeHouseholdMember(member.id)
        // Leaving navigates away; there is no list or status line left to refresh.
        if (!options?.leaving) {
          await reload()
          if (mounted.current) setStatus({ kind: 'removed', name: label })
        }
        return 'removed'
      } catch (error) {
        const code = errorCodeOf(error)
        if (code === 'members-changed' || code === 'member-not-found') {
          await reload()
          if (mounted.current) setStatus({ kind: 'membersChanged' })
          return 'changed'
        }
        if (code === 'last-member') {
          await reload()
          return 'last-member'
        }
        return 'failed'
      } finally {
        settle()
      }
    },
    [reload],
  )

  const revoke = useCallback(
    async (invite: OpenInvite): Promise<RevokeOutcome> => {
      setBusy(true)
      setStatus(null)
      setRevokeFailed(false)
      try {
        await revokeInvite(invite.id)
        await reload()
        if (mounted.current) setStatus({ kind: 'revoked' })
        return 'revoked'
      } catch (error) {
        if (errorCodeOf(error) === 'invite-not-found') {
          await reload()
          if (mounted.current) setStatus({ kind: 'inviteGone' })
          return 'gone'
        }
        if (mounted.current) setRevokeFailed(true)
        return 'failed'
      } finally {
        settle()
      }
    },
    [reload],
  )

  const notifyInviteCreated = useCallback(async () => {
    await reload()
    if (mounted.current) setStatus({ kind: 'created' })
  }, [reload])

  return { members, invites, loadError, busy, status, revokeFailed, reload, removeMember, revoke, notifyInviteCreated }
}
