import { useEffect, useState } from 'react'
import { useTranslation } from 'react-i18next'
import { ApiError, fetchJobStatus } from '@/lib/smart-plug-import-api'

export type HouseholdImportJobState = 'processing' | 'completed' | 'failed'

const POLL_INTERVAL_MS = 2000
// Tolerate a few consecutive transient network blips while polling before giving up — a single
// dropped fetch doesn't mean the backend job itself failed (mirrors use-smart-plug-import-job.ts).
const MAX_CONSECUTIVE_POLL_FAILURES = 3

export interface HouseholdImportJobResult {
  state: HouseholdImportJobState
  errorMessage: string | null
}

// Polls the existing generic GET /api/jobs/{id} endpoint to completion for the restore job the
// confirm step enqueued — same polling shape as use-smart-plug-import-job.ts (2s interval,
// tolerate a few consecutive transient failures), extracted as its own hook since this panel's
// upload -> validate -> confirm -> poll flow is a wizard driven by explicit user actions, not a
// "fires its own upload on mount" job like Smart Plug Import.
export function useHouseholdImportJobPoll(jobId: string | null): HouseholdImportJobResult {
  const { t } = useTranslation()
  const [state, setState] = useState<HouseholdImportJobState>('processing')
  const [errorMessage, setErrorMessage] = useState<string | null>(null)

  useEffect(() => {
    if (!jobId) {
      return
    }

    setState('processing')
    setErrorMessage(null)
    let cancelled = false
    let consecutiveFailures = 0

    const intervalId = window.setInterval(() => {
      void (async () => {
        try {
          const job = await fetchJobStatus(jobId)
          if (cancelled) {
            return
          }

          consecutiveFailures = 0
          if (job.status === 'completed') {
            setState('completed')
          } else if (job.status === 'failed') {
            setErrorMessage(job.errorMessage ?? t('settings.dataImport.errorGeneric'))
            setState('failed')
          }
          // 'queued'/'processing' — keep polling, no state change needed beyond the initial
          // 'processing' this hook already starts in.
        } catch (err) {
          if (cancelled) {
            return
          }

          if (err instanceof ApiError && err.status === 404) {
            // Not yet queued for processing — keep polling indefinitely, this isn't a failure.
            return
          }

          consecutiveFailures += 1
          if (consecutiveFailures >= MAX_CONSECUTIVE_POLL_FAILURES) {
            setErrorMessage(t('settings.dataImport.errorGeneric'))
            setState('failed')
          }
        }
      })()
    }, POLL_INTERVAL_MS)

    return () => {
      cancelled = true
      window.clearInterval(intervalId)
    }
  }, [jobId, t])

  return { state, errorMessage }
}
