import { useEffect, useState } from 'react'
import { useTranslation } from 'react-i18next'
import { Button } from '@/components/ui/button'
import { HouseholdCreationForm, type CreatedHousehold } from '@/components/household-creation/household-creation-form'
import { InviteGeneratePanel } from '@/components/household-invite/invite-generate-panel'
import { InviteAcceptForm } from '@/components/household-invite/invite-accept-form'
import { SettingsPage } from '@/components/settings/settings-page'

interface SessionResponse {
  hasHousehold: boolean
  householdId: string | null
  locale: string | null
  currency: string | null
}

type SessionState =
  | { status: 'loading' }
  | { status: 'unauthenticated' }
  | { status: 'error' }
  | { status: 'needs-household' }
  | { status: 'ready'; household: CreatedHousehold }

// The first URL in this repo besides "/" that the SPA shell must render distinctly. A single
// regex check is proportionate to one new path pattern — no router library added for this
// (see Story 1.5's Dev Notes on deferring react-router until genuinely needed).
const INVITE_PATH_PATTERN = /^\/join\/([^/]+)\/?$/

function App() {
  const { t } = useTranslation()
  const [state, setState] = useState<SessionState>({ status: 'loading' })
  // Local view state, not a URL route — Story 1.9's Settings surface is the first thing reachable
  // via a button rather than a bookmarkable path, matching Story 1.5's "no react-router yet"
  // precedent (see invite-accept-form.tsx's /join/{token} handling for the one existing exception,
  // which predates this and stays URL-addressable for its own reason: it must survive a full-page
  // OIDC redirect round trip).
  const [view, setView] = useState<'dashboard' | 'settings'>('dashboard')
  const inviteToken = window.location.pathname.match(INVITE_PATH_PATTERN)?.[1] ?? null

  useEffect(() => {
    let cancelled = false

    fetch('/api/session', { credentials: 'include' })
      .then((response) => {
        // The SPA's response to a 401 here is what triggers navigation to /login — a
        // server-initiated OIDC challenge, not an SPA client route (AC #1).
        if (response.status === 401) {
          if (!cancelled) {
            setState({ status: 'unauthenticated' })
          }
          return null
        }

        if (!response.ok) {
          throw new Error(`Unexpected /api/session response: ${response.status}`)
        }

        return response.json() as Promise<SessionResponse>
      })
      .then((session) => {
        if (cancelled || session === null) {
          return
        }

        setState(
          session.hasHousehold
            ? {
                status: 'ready',
                household: {
                  id: session.householdId!,
                  locale: session.locale!,
                  currency: session.currency!,
                },
              }
            : { status: 'needs-household' },
        )
      })
      .catch(() => {
        // Only a real 401 means "unauthenticated" (handled above). Any other failure — a
        // transient 5xx, a network error — must not be treated the same way: doing so would
        // force-navigate an already-authenticated user through /login, masking a real backend
        // error as a login prompt and risking a redirect loop if the backend keeps failing.
        if (!cancelled) {
          setState({ status: 'error' })
        }
      })

    return () => {
      cancelled = true
    }
  }, [])

  useEffect(() => {
    if (state.status === 'unauthenticated') {
      // Preserve /join/{token} across the OIDC round trip — otherwise the invited person gets
      // bounced to "/" after login and loses their invite link entirely (AC #1).
      window.location.href = inviteToken
        ? `/login?returnUrl=${encodeURIComponent(window.location.pathname)}`
        : '/login'
    }
  }, [state.status, inviteToken])

  if (state.status === 'loading' || state.status === 'unauthenticated') {
    return (
      <main className="flex min-h-svh flex-col items-center justify-center gap-4">
        <p>{t('session.loading')}</p>
      </main>
    )
  }

  if (state.status === 'error') {
    return (
      <main className="flex min-h-svh flex-col items-center justify-center gap-4">
        <p className="text-destructive">{t('session.error')}</p>
      </main>
    )
  }

  if (state.status === 'needs-household') {
    if (inviteToken) {
      return (
        <InviteAcceptForm
          token={inviteToken}
          onJoined={(household) => {
            // Clear the invite path so the "ready" branch below doesn't mistake this freshly
            // joined member for someone visiting a stale/foreign invite link (inviteToken is
            // re-derived from window.location.pathname on every render, not stored in state).
            window.history.replaceState({}, '', '/')
            setState({ status: 'ready', household })
          }}
        />
      )
    }

    return (
      <HouseholdCreationForm
        onCreated={(household) => setState({ status: 'ready', household })}
      />
    )
  }

  // A principal that already has a Household visiting a stale/foreign invite link — graceful
  // handling, not a feature (matches the product's "never a silently-ignored state" discipline).
  if (inviteToken) {
    return (
      <main className="flex min-h-svh flex-col items-center justify-center gap-4 p-4">
        <p className="text-muted-foreground text-sm">{t('householdInvite.alreadyInHousehold')}</p>
        <a href="/" className="text-sm underline underline-offset-4">
          {t('householdInvite.backToApp')}
        </a>
      </main>
    )
  }

  if (view === 'settings') {
    return <SettingsPage onBack={() => setView('dashboard')} />
  }

  // Real Dashboard is Epic 2, out of scope here — AC #1 only requires "never a broken or empty
  // dashboard," not a built one. This placeholder is Story 1.1's existing skeleton content.
  return (
    <main className="flex min-h-svh flex-col items-center justify-center gap-4">
      <h1 className="text-2xl font-semibold">{t('app.title')}</h1>
      <Button>{t('shell.placeholder')}</Button>
      <InviteGeneratePanel />
      <Button variant="outline" onClick={() => setView('settings')}>
        {t('settings.heading')}
      </Button>
    </main>
  )
}

export default App
