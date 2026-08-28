import { useEffect, useRef, useState } from 'react'
import { useTranslation } from 'react-i18next'
import { ApiError, fetchJobStatus, uploadSmartPlugFile, type SmartPlugImportGapDto } from '@/lib/smart-plug-import-api'

export type ImportJobState = 'uploading' | 'processing' | 'completed' | 'awaitingMapping' | 'flaggedForReview' | 'failed'

const POLL_INTERVAL_MS = 2000
// Tolerate a few consecutive transient network blips while polling before giving up — a single
// dropped fetch doesn't mean the backend job itself failed.
const MAX_CONSECUTIVE_POLL_FAILURES = 3

export interface SmartPlugImportJob {
  state: ImportJobState
  error: string | null
  gaps: SmartPlugImportGapDto[]
  // A 404 while polling means the background worker hasn't dequeued this job yet (this system
  // processes one job at a time — a large prior import can leave a fresh job queued for minutes),
  // not that it failed. Tracked separately from `error` so it never renders as a failure state.
  queued: boolean
  smartPlugImportId: string | null
  deviceTag: string
  refreshAfterMapping: () => Promise<void>
}

// One instance per queue item (Story 3.5's rewrite of Story 3.1's single-file
// SmartPlugImportPanel): fires its own upload on mount and, once accepted, runs its own polling
// loop — so several instances mounted at once upload concurrently and poll independently,
// exactly what AC #4/#5 require. This is the same upload+poll+state-machine shape Story 3.1
// shipped inline in one component, extracted unchanged so each queue item gets its own closure
// over its own jobId (AD-6).
export function useSmartPlugImportJob(file: File): SmartPlugImportJob {
  const { t } = useTranslation()
  const [state, setState] = useState<ImportJobState>('uploading')
  const [error, setError] = useState<string | null>(null)
  const [gaps, setGaps] = useState<SmartPlugImportGapDto[]>([])
  const [queued, setQueued] = useState(false)
  const [smartPlugImportId, setSmartPlugImportId] = useState<string | null>(null)
  const [deviceTag, setDeviceTag] = useState('')
  const jobIdRef = useRef<string | null>(null)

  useEffect(() => {
    // An AbortController (not just a `cancelled` boolean) is required here: React StrictMode's
    // dev-only mount→cleanup→remount double-invoke would otherwise let the first invocation's
    // `uploadSmartPlugFile` call reach the network before its cleanup runs, firing two real
    // uploads/jobs for one file selection. Aborting the in-flight `fetch` on cleanup cancels the
    // first request before it completes, so only the surviving (second) invocation's upload
    // actually goes through — this also correctly cancels an in-flight upload on a genuine
    // unmount (e.g. the user removes this item or leaves the page mid-upload).
    const controller = new AbortController()

    uploadSmartPlugFile(file, controller.signal)
      .then((jobId) => {
        jobIdRef.current = jobId
        setState('processing')
      })
      .catch((err) => {
        if (controller.signal.aborted) {
          return
        }
        if (err instanceof ApiError && err.status === 400) {
          setError(t('smartPlugImport.errorUnsupportedType'))
        } else {
          setError(t('smartPlugImport.errorGeneric'))
        }
        setState('failed')
      })

    return () => {
      controller.abort()
    }
    // `file` is fixed for the lifetime of one hook instance (one instance per queue item) — this
    // effect intentionally runs once per mount, not on every render.
  }, [])

  useEffect(() => {
    if (state !== 'processing' || !jobIdRef.current) {
      return
    }

    let cancelled = false
    let consecutiveFailures = 0
    const jobId = jobIdRef.current

    const intervalId = window.setInterval(async () => {
      try {
        const job = await fetchJobStatus(jobId)
        if (cancelled) {
          return
        }

        consecutiveFailures = 0
        if (job.status === 'queued') {
          // Review-round-2 patch (Story 3.6): the backend now inserts a Queued row at enqueue
          // time, so this job already exists and 200s here instead of 404ing — the old
          // 404-means-queued heuristic below never fires for it anymore. Same "Waiting" signal,
          // read from the status body instead.
          setQueued(true)
          return
        }
        setQueued(false)
        if (job.status === 'completed') {
          setGaps(job.gaps ?? [])
          if (job.importStatus === 'awaitingpowerpointmapping') {
            setSmartPlugImportId(job.smartPlugImportId)
            setDeviceTag(job.smartPlugImportDeviceTag ?? '')
            setState('awaitingMapping')
          } else if (job.importStatus === 'flaggedforreview') {
            setState('flaggedForReview')
          } else {
            setState('completed')
          }
        } else if (job.status === 'failed') {
          setError(job.errorMessage ?? t('smartPlugImport.errorGeneric'))
          setState('failed')
        }
      } catch (err) {
        if (cancelled) {
          return
        }

        if (err instanceof ApiError && err.status === 404) {
          // Not yet queued for processing — keep polling indefinitely, this isn't a failure.
          setQueued(true)
          return
        }

        consecutiveFailures += 1
        if (consecutiveFailures >= MAX_CONSECUTIVE_POLL_FAILURES) {
          setError(t('smartPlugImport.errorGeneric'))
          setState('failed')
        }
      }
    }, POLL_INTERVAL_MS)

    return () => {
      cancelled = true
      window.clearInterval(intervalId)
    }
  }, [state, t])

  const refreshAfterMapping = async () => {
    setState('completed')
    // The mapping call itself doesn't return a gap list (Task 5) — re-poll the same job status
    // endpoint to pick up gaps detected during mapping completion (AD-7's second path). Retries a
    // few times on a transient network blip rather than silently giving up after one attempt —
    // the import itself already completed successfully either way, so this never blocks the UI.
    if (!jobIdRef.current) {
      return
    }

    for (let attempt = 1; attempt <= MAX_CONSECUTIVE_POLL_FAILURES; attempt += 1) {
      try {
        const job = await fetchJobStatus(jobIdRef.current)
        setGaps(job.gaps ?? [])
        return
      } catch {
        if (attempt < MAX_CONSECUTIVE_POLL_FAILURES) {
          await new Promise((resolve) => window.setTimeout(resolve, POLL_INTERVAL_MS))
        }
      }
    }
  }

  return { state, error, gaps, queued, smartPlugImportId, deviceTag, refreshAfterMapping }
}
