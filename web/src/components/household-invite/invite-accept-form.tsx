import { useEffect, useState } from 'react'
import { useTranslation } from 'react-i18next'
import { Button } from '@/components/ui/button'
import type { CreatedHousehold } from '@/components/household-creation/household-creation-form'

interface InviteAcceptFormProps {
  token: string
  onJoined: (household: CreatedHousehold) => void
}

type AcceptFormState = 'checking' | 'valid' | 'invalid' | 'error' | 'accepting'

export function InviteAcceptForm({ token, onJoined }: InviteAcceptFormProps) {
  const { t } = useTranslation()
  const [state, setState] = useState<AcceptFormState>('checking')

  useEffect(() => {
    let cancelled = false

    fetch(`/api/household-invites/${token}`, { credentials: 'include' })
      .then((response) => {
        if (cancelled) {
          return
        }

        if (response.ok) {
          setState('valid')
          return
        }

        // 404 (unknown token) and 409 (expired/consumed) both mean "not usable" — neither
        // becomes valid by retrying, so both collapse into the same invalid copy. Anything
        // else (a transient 5xx) is a real backend error, not an invalid invite — showing it
        // as "invalid" would send a legitimate invitee's working link to a dead end.
        setState(response.status === 404 || response.status === 409 ? 'invalid' : 'error')
      })
      .catch(() => {
        if (!cancelled) {
          setState('error')
        }
      })

    return () => {
      cancelled = true
    }
  }, [token])

  const handleAccept = async () => {
    setState('accepting')

    try {
      const response = await fetch(`/api/household-invites/${token}/accept`, {
        method: 'POST',
        credentials: 'include',
      })

      if (!response.ok) {
        // Covers the lost-the-race case too: a second, near-simultaneous accept of the same
        // single-use invite also comes back 409. Only 404/409 mean the invite itself is
        // unusable; anything else is a transient backend failure, not an invalid invite.
        setState(response.status === 404 || response.status === 409 ? 'invalid' : 'error')
        return
      }

      const household = (await response.json()) as CreatedHousehold
      onJoined(household)
    } catch {
      setState('error')
    }
  }

  if (state === 'checking') {
    return (
      <main className="flex min-h-svh flex-col items-center justify-center gap-4">
        <p>{t('session.loading')}</p>
      </main>
    )
  }

  if (state === 'invalid' || state === 'error') {
    return (
      <main className="flex min-h-svh flex-col items-center justify-center gap-4 p-4">
        <p className="text-destructive text-sm">
          {t(state === 'invalid' ? 'householdInvite.invalid' : 'householdInvite.error')}
        </p>
        <a href="/" className="text-sm underline underline-offset-4">
          {t('householdInvite.backToApp')}
        </a>
      </main>
    )
  }

  return (
    <main className="flex min-h-svh flex-col items-center justify-center gap-6 p-4">
      <div className="flex w-full max-w-sm flex-col gap-6">
        <div className="flex flex-col gap-1 text-center">
          <h1 className="text-2xl font-semibold">{t('householdInvite.acceptHeading')}</h1>
          <p className="text-muted-foreground text-sm">{t('householdInvite.acceptDescription')}</p>
        </div>

        {/* Never auto-accept on mount (FR-1's single-confirmation-tap pattern) — a silent side
            effect on load would be surprising UX, and it removes the last line of defense
            against a preview-bot consuming a single-use invite before the real person acts. */}
        <Button onClick={handleAccept} disabled={state === 'accepting'}>
          {state === 'accepting' ? t('householdInvite.accepting') : t('householdInvite.acceptButton')}
        </Button>
      </div>
    </main>
  )
}
