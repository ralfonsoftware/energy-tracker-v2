import { useId, useRef, useState } from 'react'
import { useTranslation } from 'react-i18next'
import { CircleCheck, Info, Link2, Link2Off, LogOut, RefreshCw, TriangleAlert, User, UserMinus } from 'lucide-react'
import { Button } from '@/components/ui/button'
import { GlassCard } from '@/components/ui/glass-card'
import { Skeleton } from '@/components/ui/skeleton'
import { InviteMemberRow } from '@/components/household-invite/invite-member-row'
import type { HouseholdMember, OpenInvite } from '@/lib/household-members-api'
import { LeaveHouseholdDialog } from './leave-household-dialog'
import { RemoveMemberDialog } from './remove-member-dialog'
import { ACCENT_SURFACE, ACCENT_TEXT, ERR_BOX, ERR_TEXT } from './styles'
import { useHouseholdMembers, type MembersStatus } from './use-household-members'

interface HouseholdMembersSectionProps {
  householdId: string
}


function Count({ value }: { value: number }) {
  return (
    <span className="rounded-full border border-border px-[7px] py-px text-[10.5px] font-bold tracking-normal text-muted-foreground normal-case">
      {value}
    </span>
  )
}

function Avatar({ name, icon }: { name?: string; icon?: 'link' | 'person' }) {
  const neutral = icon !== undefined
  return (
    <span
      aria-hidden="true"
      className={`flex size-8 shrink-0 items-center justify-center rounded-full text-[13px] font-bold ${
        neutral ? 'border border-border bg-muted text-muted-foreground' : `${ACCENT_SURFACE} ${ACCENT_TEXT}`
      }`}
    >
      {icon === 'link' && <Link2 className="size-4" />}
      {icon === 'person' && <User className="size-4" />}
      {!neutral && name?.charAt(0).toUpperCase()}
    </span>
  )
}

function StatusLine({ status, label }: { status: MembersStatus | null; label: (kind: MembersStatus) => string }) {
  // The live region is mounted up front, empty, so a screen reader announces the text when it appears.
  return (
    <div role="status">
      {status && (
        <p className={`flex items-center gap-2 rounded-[10px] px-3 py-2 text-[12.5px] leading-snug ${ACCENT_SURFACE}`}>
          <CircleCheck aria-hidden="true" className={`size-4 shrink-0 ${ACCENT_TEXT}`} />
          <span>{label(status)}</span>
        </p>
      )}
    </div>
  )
}

function ErrorLine({ children }: { children: React.ReactNode }) {
  return (
    <p role="alert" className={`flex items-start gap-2 rounded-[10px] px-3 py-2 text-[12.5px] leading-snug ${ERR_BOX}`}>
      <TriangleAlert aria-hidden="true" className="mt-px size-4 shrink-0" />
      <span>{children}</span>
    </p>
  )
}

// Story 11.9 (AD-26: full, equal trust, no roles): the Household section of Settings. Members with Remove / Leave, open
// invites with Revoke, and the existing "Invite a member" control. Layout, copy and states follow
// mockups/key-household-members-2026-10-17.html (approved 2026-10-18).
export function HouseholdMembersSection({ householdId }: HouseholdMembersSectionProps) {
  const { t, i18n } = useTranslation()
  const { members, invites, loadError, busy, status, revokeFailed, reload, removeMember, revoke, notifyInviteCreated } =
    useHouseholdMembers()
  const [removeTarget, setRemoveTarget] = useState<HouseholdMember | null>(null)
  const [removeError, setRemoveError] = useState<'generic' | 'lastMember' | null>(null)
  const [leaving, setLeaving] = useState(false)
  const headingRef = useRef<HTMLHeadingElement>(null)
  const soleReasonId = useId()

  const dateFormat = new Intl.DateTimeFormat(i18n.language, { dateStyle: 'medium' })
  const formatDate = (iso: string) => dateFormat.format(new Date(iso))
  const labelOf = (member: HouseholdMember) => member.displayName?.trim() || t('householdMembers.genericMember')
  const statusText = (s: MembersStatus) => {
    switch (s.kind) {
      case 'removed':
        return t('householdMembers.status.removed', { name: s.name })
      case 'revoked':
        return t('householdMembers.status.revoked')
      case 'created':
        return t('householdMembers.status.created')
      case 'membersChanged':
        return t('householdMembers.status.membersChanged')
      case 'inviteGone':
        return t('householdMembers.status.inviteGone')
    }
  }

  const me = members?.find((m) => m.isCurrentMember) ?? null
  const sole = members !== null && members.length === 1

  // The row that held focus is gone after a removal or revoke, so focus moves to the section heading (AC #16).
  const focusHeading = () => headingRef.current?.focus()

  const openRemove = (member: HouseholdMember) => {
    setRemoveError(null)
    setRemoveTarget(member)
  }

  const confirmRemove = async () => {
    if (!removeTarget) return
    setRemoveError(null)
    const outcome = await removeMember(removeTarget, labelOf(removeTarget))
    if (outcome === 'failed') {
      setRemoveError('generic')
    } else if (outcome === 'last-member') {
      setRemoveError('lastMember')
    } else {
      setRemoveTarget(null)
      focusHeading()
    }
  }

  const handleRevoke = async (invite: OpenInvite) => {
    const outcome = await revoke(invite)
    if (outcome !== 'failed') focusHeading()
  }

  const handleLeave = async () => (me ? removeMember(me, labelOf(me), { leaving: true }) : 'failed' as const)

  const closeLeave = () => {
    setLeaving(false)
    focusHeading()
  }

  return (
    <GlassCard className="flex flex-col gap-3.5" data-slot="household-members">
      <h2
        ref={headingRef}
        tabIndex={-1}
        className="text-[12px] font-bold tracking-[0.6px] text-muted-foreground uppercase outline-none"
      >
        {t('settings.sections.household')}
      </h2>

      <StatusLine status={status} label={statusText} />

      {loadError && (
        <div role="alert" className={`flex flex-wrap items-center gap-2 rounded-[10px] px-3 py-2 text-[12.5px] ${ERR_BOX}`}>
          <TriangleAlert aria-hidden="true" className="size-4 shrink-0" />
          <span>{t('householdMembers.error.load')}</span>
          <Button type="button" variant="ghost" size="sm" className="ml-auto text-primary" onClick={() => void reload()}>
            <RefreshCw aria-hidden="true" />
            {t('householdMembers.retry')}
          </Button>
        </div>
      )}

      {!loadError && (members === null || invites === null) && (
        <div aria-busy="true" className="flex flex-col gap-2">
          <p className="sr-only">{t('householdMembers.loading')}</p>
          <Skeleton className="h-10 w-full" />
          <Skeleton className="h-10 w-full" />
        </div>
      )}

      {members && invites && (
        <>
          <div>
            <h3 className="mb-1.5 flex items-center gap-2 text-[11.5px] font-bold tracking-[0.5px] text-muted-foreground uppercase">
              {t('householdMembers.membersHeading')}
              <Count value={members.length} />
            </h3>
            <ul className="divide-y divide-[rgba(40,70,50,0.12)] dark:divide-[rgba(210,235,220,0.1)]">
              {members.map((member) => {
                const label = labelOf(member)
                const highlighted = removeTarget?.id === member.id || (leaving && member.isCurrentMember)
                return (
                  <li
                    key={member.id}
                    className={`-mx-2 flex items-center gap-3 rounded-xl px-2 py-2.5 ${
                      highlighted ? 'bg-[rgba(30,122,97,0.09)] ring-1 ring-[#1E7A61] dark:bg-[rgba(111,209,177,0.1)] dark:ring-[#6FD1B1]' : ''
                    }`}
                  >
                    <Avatar name={member.displayName?.trim() || undefined} icon={member.displayName?.trim() ? undefined : 'person'} />
                    <span className="flex min-w-0 flex-1 flex-col gap-0.5">
                      <span className={`flex flex-wrap items-center gap-2 text-[13.5px] ${member.displayName?.trim() ? 'font-semibold' : 'font-medium text-muted-foreground'}`}>
                        {label}
                        {member.isCurrentMember && (
                          <span className={`rounded-full px-2 py-px text-[10.5px] font-bold tracking-[0.3px] ${ACCENT_SURFACE} ${ACCENT_TEXT}`}>
                            {t('householdMembers.you')}
                          </span>
                        )}
                      </span>
                      <span className="text-xs leading-snug text-muted-foreground">
                        {t('householdMembers.joined', { date: formatDate(member.joinedAtUtc) })}
                      </span>
                    </span>
                    {member.isCurrentMember ? (
                      <Button
                        type="button"
                        variant="outline"
                        aria-label={t('householdMembers.leaveAria')}
                        aria-disabled={sole || undefined}
                        aria-describedby={sole ? soleReasonId : undefined}
                        disabled={busy}
                        className={`h-9 shrink-0 gap-1.5 rounded-[10px] bg-transparent px-3 ${
                          sole
                            ? 'border-dashed text-muted-foreground opacity-50'
                            : `${ERR_TEXT} border-[rgba(176,50,40,0.3)] hover:bg-[rgba(176,50,40,0.1)] dark:border-[rgba(235,149,140,0.35)] dark:hover:bg-[rgba(235,149,140,0.16)]`
                        }`}
                        onClick={() => {
                          if (!sole) setLeaving(true)
                        }}
                      >
                        <LogOut aria-hidden="true" className="size-[17px]" />
                        {t('householdMembers.leave')}
                      </Button>
                    ) : (
                      <Button
                        type="button"
                        variant="outline"
                        size="icon"
                        aria-label={t('householdMembers.removeAria', { name: label })}
                        disabled={busy}
                        className="size-9 shrink-0 rounded-[10px] text-muted-foreground"
                        onClick={() => openRemove(member)}
                      >
                        <UserMinus aria-hidden="true" className="size-[18px]" />
                      </Button>
                    )}
                  </li>
                )
              })}
            </ul>
            {sole && (
              <p id={soleReasonId} className="mt-2 flex items-start gap-2 rounded-[10px] border border-border bg-muted/40 px-3 py-2 text-[12.5px] leading-snug text-muted-foreground">
                <Info aria-hidden="true" className={`mt-px size-4 shrink-0 ${ACCENT_TEXT}`} />
                <span>{t('householdMembers.soleMember')}</span>
              </p>
            )}
          </div>

          <div>
            <h3 className="mb-1.5 flex items-center gap-2 text-[11.5px] font-bold tracking-[0.5px] text-muted-foreground uppercase">
              {t('householdMembers.invitesHeading')}
              <Count value={invites.length} />
            </h3>
            {revokeFailed && (
              <div className="mb-2">
                <ErrorLine>{t('householdMembers.error.revokeGeneric')}</ErrorLine>
              </div>
            )}
            {invites.length > 0 ? (
              <>
                <ul className="divide-y divide-[rgba(40,70,50,0.12)] dark:divide-[rgba(210,235,220,0.1)]">
                  {invites.map((invite) => {
                    const created = formatDate(invite.createdAtUtc)
                    return (
                      <li key={invite.id} className="-mx-2 flex items-center gap-3 px-2 py-2.5">
                        <Avatar icon="link" />
                        <span className="flex min-w-0 flex-1 flex-col gap-0.5">
                          <span className="text-[13.5px] font-semibold">{t('householdInvite.linkLabel')}</span>
                          <span className="text-xs leading-snug text-muted-foreground">
                            {t('householdMembers.inviteSub', { created, expires: formatDate(invite.expiresAtUtc) })}
                          </span>
                        </span>
                        <Button
                          type="button"
                          variant="outline"
                          size="icon"
                          aria-label={t('householdMembers.revokeAria', { created })}
                          disabled={busy}
                          className="size-9 shrink-0 rounded-[10px] text-muted-foreground"
                          onClick={() => void handleRevoke(invite)}
                        >
                          <Link2Off aria-hidden="true" className="size-[18px]" />
                        </Button>
                      </li>
                    )
                  })}
                </ul>
                <p className="mt-2 text-[11.5px] leading-snug text-muted-foreground">{t('householdMembers.invitesHint')}</p>
              </>
            ) : (
              <p className="flex items-start gap-2.5 rounded-xl border border-dashed border-border px-3 py-2.5 text-[12.5px] leading-snug text-muted-foreground">
                <Link2 aria-hidden="true" className="mt-px size-4 shrink-0" />
                <span>{t('householdMembers.emptyInvites')}</span>
              </p>
            )}
          </div>
        </>
      )}

      <div className="border-t border-border pt-3 wide:border-t-0 wide:pt-0">
        <InviteMemberRow onCreated={() => void notifyInviteCreated()} />
      </div>

      <RemoveMemberDialog
        member={removeTarget}
        label={removeTarget ? labelOf(removeTarget) : ''}
        joinedOn={removeTarget ? formatDate(removeTarget.joinedAtUtc) : ''}
        busy={busy}
        error={removeError}
        onCancel={() => setRemoveTarget(null)}
        onConfirm={() => void confirmRemove()}
      />

      {leaving && <LeaveHouseholdDialog householdId={householdId} busy={busy} onClose={closeLeave} onLeave={handleLeave} />}
    </GlassCard>
  )
}
