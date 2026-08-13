import { useEffect, useState } from 'react'
import { useTranslation } from 'react-i18next'
import { Button } from '@/components/ui/button'
import { HouseholdCreationForm, type CreatedHousehold } from '@/components/household-creation/household-creation-form'

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

function App() {
  const { t } = useTranslation()
  const [state, setState] = useState<SessionState>({ status: 'loading' })

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
      window.location.href = '/login'
    }
  }, [state.status])

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
    return (
      <HouseholdCreationForm
        onCreated={(household) => setState({ status: 'ready', household })}
      />
    )
  }

  // Real Dashboard is Epic 2, out of scope here — AC #1 only requires "never a broken or empty
  // dashboard," not a built one. This placeholder is Story 1.1's existing skeleton content.
  return (
    <main className="flex min-h-svh flex-col items-center justify-center gap-4">
      <h1 className="text-2xl font-semibold">{t('app.title')}</h1>
      <Button>{t('shell.placeholder')}</Button>
    </main>
  )
}

export default App
