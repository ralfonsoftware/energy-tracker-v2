import { apiFetch, toApiError } from '@/lib/api-client'

// Story 11.9 (AC #7; AD-26): members, leaving and open invites. A non-2xx response throws an ApiError
// whose errorCode (last-member, members-changed, member-not-found, invite-not-found) the UI localizes.
// Removing and leaving are the same call: the id is the caller's own member id when leaving.
export interface HouseholdMember {
  id: string
  displayName: string | null
  isCurrentMember: boolean
  joinedAtUtc: string
}

// An open invite never carries a token or a link: only a hash is stored, so a link is shown once, at creation.
export interface OpenInvite {
  id: string
  createdAtUtc: string
  expiresAtUtc: string
}

export async function fetchHouseholdMembers(signal?: AbortSignal): Promise<HouseholdMember[]> {
  const response = await apiFetch('/api/household-members', { signal })
  if (!response.ok) throw await toApiError(response)
  const body = (await response.json()) as { members?: unknown } | null
  // A 200 with another shape (a proxy's HTML or an unexpected body) is a failed load, not an empty household.
  if (!Array.isArray(body?.members)) throw new Error('Unexpected /api/household-members response body')
  return body.members as HouseholdMember[]
}

export async function removeHouseholdMember(memberId: string): Promise<void> {
  const response = await apiFetch(`/api/household-members/${memberId}`, { method: 'DELETE' })
  if (!response.ok) throw await toApiError(response)
}

export async function fetchOpenInvites(signal?: AbortSignal): Promise<OpenInvite[]> {
  const response = await apiFetch('/api/household-invites', { signal })
  if (!response.ok) throw await toApiError(response)
  const body = (await response.json()) as { invites?: unknown } | null
  if (!Array.isArray(body?.invites)) throw new Error('Unexpected /api/household-invites response body')
  return body.invites as OpenInvite[]
}

export async function revokeInvite(inviteId: string): Promise<void> {
  const response = await apiFetch(`/api/household-invites/${inviteId}`, { method: 'DELETE' })
  if (!response.ok) throw await toApiError(response)
}
