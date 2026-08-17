import { useCallback, useEffect, useRef, useState } from 'react'
import { useTranslation } from 'react-i18next'
import { HouseholdCreationForm, type CreatedHousehold } from '@/components/household-creation/household-creation-form'
import { InviteAcceptForm } from '@/components/household-invite/invite-accept-form'
import { SettingsPage } from '@/components/settings/settings-page'
import { DashboardPage } from '@/components/dashboard/dashboard-page'
import { registerOfflineSync } from '@/lib/meter-reading-sync'
import { fetchOpenMeterRegressionPrompt, type MeterRegressionPromptDto } from '@/lib/meter-regression-api'
import { fetchCurrentStatus, type StatusDto } from '@/lib/status-api'

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
  const [openRegressionPrompt, setOpenRegressionPrompt] = useState<MeterRegressionPromptDto | null>(null)
  const [logSheetOpen, setLogSheetOpen] = useState(false)
  const [status, setStatus] = useState<StatusDto | null>(null)
  const [statusLoading, setStatusLoading] = useState(true)

  // Tracks the last Status value the entrance/specular-sweep animation already played for, in a
  // ref that lives here (App never unmounts) rather than inside StatusCard/DashboardPage (which
  // do — the whole subtree unmounts and remounts on every Settings round trip). Without this,
  // there's no way to tell "Status actually changed" apart from "the component just remounted
  // after an unrelated navigation" — a remount always looks identical to a fresh mount from the
  // inside, so the animation would replay every time the user returns from Settings (AC #6 only
  // wants it on cold load or a real recompute).
  const lastAnimatedStatusFingerprintRef = useRef<string | null>(null)
  const statusFingerprint = status
    ? `${status.status}-${status.paceToDateKwh}-${status.baselineToDateKwh}-${status.isLowConfidence}`
    : null
  const playStatusEntranceAnimation =
    statusFingerprint !== null && statusFingerprint !== lastAnimatedStatusFingerprintRef.current

  useEffect(() => {
    lastAnimatedStatusFingerprintRef.current = statusFingerprint
  }, [statusFingerprint])

  // Mirrors refreshOpenRegressionPrompt below: re-fetches the live, request-time Status (AD-7)
  // after anything that could change it, rather than polling on a timer. A fetch failure
  // degrades to the same onboarding-empty treatment as a genuinely undefined Status (Task 5) —
  // never a stuck skeleton, never a fabricated real state.
  const refreshStatus = useCallback(async () => {
    try {
      const result = await fetchCurrentStatus()
      setStatus(result)
    } catch {
      setStatus(null)
    } finally {
      setStatusLoading(false)
    }
  }, [])

  // Single source of truth for "what's currently open" (AC #7): re-polling after every save/
  // resolve, rather than trusting the create-reading response, mirrors the "drill-down data is
  // always a separate endpoint" convention already used for Status.
  const refreshOpenRegressionPrompt = useCallback(async () => {
    try {
      const prompt = await fetchOpenMeterRegressionPrompt()
      setOpenRegressionPrompt(prompt)
      if (prompt) {
        // A newly-raised (or still-open) prompt supersedes the Log Reading sheet rather than
        // stacking on top of it — force it closed in the same state update.
        setLogSheetOpen(false)
      }
    } catch {
      // Best-effort — a transient failure here just means the prompt (if any) surfaces on the
      // next poll/mount instead of immediately; it must not block the rest of the dashboard.
    }
  }, [])

  const handleLogSheetOpenChange = (next: boolean) => {
    if (next && openRegressionPrompt) {
      return
    }
    setLogSheetOpen(next)
  }

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
    // Only once a Household session is confirmed — flushing against an unauthenticated or
    // still-resolving session would just churn on 401s until the queued reading's owner is known.
    if (state.status !== 'ready') {
      return
    }

    // A reading synced from the offline queue in the background can itself raise a regression
    // (AC #1) or change Status — nothing else re-polls for either, so both are wired to the same
    // refresh a foreground save/resolve triggers.
    return registerOfflineSync(() => {
      void refreshOpenRegressionPrompt()
      void refreshStatus()
    })
  }, [state.status, refreshOpenRegressionPrompt, refreshStatus])

  useEffect(() => {
    if (state.status !== 'ready') {
      return
    }
    void refreshOpenRegressionPrompt()
    void refreshStatus()
  }, [state.status, refreshOpenRegressionPrompt, refreshStatus])

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
    return <SettingsPage householdId={state.household.id} onBack={() => setView('dashboard')} />
  }

  return (
    <DashboardPage
      household={state.household}
      status={status}
      statusLoading={statusLoading}
      playStatusEntranceAnimation={playStatusEntranceAnimation}
      logSheetOpen={logSheetOpen}
      onLogSheetOpenChange={handleLogSheetOpenChange}
      onReadingSaved={() => {
        void refreshOpenRegressionPrompt()
        void refreshStatus()
      }}
      openRegressionPrompt={openRegressionPrompt}
      onRegressionResolved={() => {
        void refreshOpenRegressionPrompt()
        void refreshStatus()
      }}
      onSettingsClick={() => setView('settings')}
    />
  )
}

export default App
