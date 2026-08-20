import { useEffect, useRef, useState } from 'react'
import { useTranslation } from 'react-i18next'
import { TriangleAlert } from 'lucide-react'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { GlassCard } from '@/components/ui/glass-card'
import { GapCard } from '@/components/smart-plug-import/gap-card'
import { PowerPointMappingDialog } from '@/components/smart-plug-import/power-point-mapping-dialog'
import { ApiError, fetchJobStatus, uploadSmartPlugFile, type SmartPlugImportGapDto } from '@/lib/smart-plug-import-api'

type ImportState = 'idle' | 'uploading' | 'processing' | 'completed' | 'awaitingMapping' | 'flaggedForReview' | 'failed'

const POLL_INTERVAL_MS = 2000
// Tolerate a few consecutive transient network blips while polling before giving up — a single
// dropped fetch doesn't mean the backend job itself failed.
const MAX_CONSECUTIVE_POLL_FAILURES = 3

// Mockup reference: key-smart-plug-import.html State 1 ("Uploading, non-blocking") — the
// dropzone, file-choose control, processing pill, and async-note copy. State 3 (create/map
// prompt) is Story 3.2's PowerPointMappingDialog; States 2/4/5 (gap cards, flagged-for-review
// banner) are Story 3.3's GapCard, rendered here whenever the polled job carries any gaps.
// Colors are deliberately plain shadcn Badge variants, not the mockup's own status-triad colors —
// the UX rubric review flagged that reuse as a DESIGN.md violation (non-status badge borrowing
// Status semantic colors) — GapCard's own amber tint is the one deliberate exception (AC #8).
export function SmartPlugImportPanel() {
  const { t } = useTranslation()
  const [state, setState] = useState<ImportState>('idle')
  const [fileName, setFileName] = useState<string | null>(null)
  const [error, setError] = useState<string | null>(null)
  const fileInputRef = useRef<HTMLInputElement>(null)
  const jobIdRef = useRef<string | null>(null)
  const smartPlugImportIdRef = useRef<string | null>(null)
  const deviceTagRef = useRef<string>('')
  const [gaps, setGaps] = useState<SmartPlugImportGapDto[]>([])
  // A 404 while polling means the background worker hasn't dequeued this job yet (this system
  // processes one job at a time — a large prior import can leave a fresh job queued for minutes),
  // not that it failed. Tracked separately from `error` so it never renders as a failure state.
  const [queued, setQueued] = useState(false)

  // First polling UI in this codebase (no existing precedent) — cleared on unmount and on
  // reaching a terminal job status (AC #2).
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
        setQueued(false)
        if (job.status === 'completed') {
          setGaps(job.gaps ?? [])
          if (job.importStatus === 'awaitingpowerpointmapping') {
            smartPlugImportIdRef.current = job.smartPlugImportId
            deviceTagRef.current = job.smartPlugImportDeviceTag ?? ''
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

  const handleFileSelected = async (file: File) => {
    setFileName(file.name)
    setError(null)
    setState('uploading')

    try {
      const jobId = await uploadSmartPlugFile(file)
      jobIdRef.current = jobId
      setState('processing')
    } catch (err) {
      setState('idle')
      if (err instanceof ApiError && err.status === 400) {
        setError(t('smartPlugImport.errorUnsupportedType'))
      } else {
        setError(t('smartPlugImport.errorGeneric'))
      }
    }
  }

  const handleInputChange = (event: React.ChangeEvent<HTMLInputElement>) => {
    const file = event.target.files?.[0]
    event.target.value = ''
    if (file) {
      void handleFileSelected(file)
    }
  }

  const handleDrop = (event: React.DragEvent<HTMLDivElement>) => {
    event.preventDefault()
    const file = event.dataTransfer.files?.[0]
    if (file) {
      void handleFileSelected(file)
    }
  }

  const handleReset = () => {
    setState('idle')
    setFileName(null)
    setError(null)
    setGaps([])
    setQueued(false)
    jobIdRef.current = null
    smartPlugImportIdRef.current = null
    deviceTagRef.current = ''
  }

  const handleMapped = async () => {
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

  return (
    <GlassCard className="flex w-full max-w-sm flex-col gap-3">
      <h2 className="text-lg font-semibold">{t('smartPlugImport.heading')}</h2>

      {state === 'idle' && (
        <div
          className="flex flex-col items-center gap-2 rounded-[12px] border border-dashed border-[rgba(40,70,50,0.2)] p-4 text-center dark:border-[rgba(210,235,220,0.2)]"
          onDragOver={(event) => event.preventDefault()}
          onDrop={handleDrop}
        >
          <p className="text-sm">{t('smartPlugImport.dropHint')}</p>
          <p className="text-muted-foreground text-xs">{t('smartPlugImport.formats')}</p>
          <Button type="button" variant="outline" size="sm" onClick={() => fileInputRef.current?.click()}>
            {t('smartPlugImport.chooseFile')}
          </Button>
          <input
            ref={fileInputRef}
            type="file"
            accept=".xlsx,.csv"
            className="sr-only"
            onChange={handleInputChange}
            aria-label={t('smartPlugImport.chooseFile')}
          />
        </div>
      )}

      {state !== 'idle' && (
        <div className="flex flex-col gap-2" aria-live="polite">
          <div className="flex items-center justify-between">
            <span className="truncate text-sm">{fileName}</span>
            {state === 'uploading' && <Badge variant="outline">{t('smartPlugImport.uploading')}</Badge>}
            {state === 'processing' && <Badge variant="outline">{t('smartPlugImport.processingBadge')}</Badge>}
            {state === 'completed' && <Badge variant="secondary">{t('smartPlugImport.completeTitle')}</Badge>}
            {state === 'awaitingMapping' && <Badge variant="outline">{t('smartPlugImport.awaitingMappingTitle')}</Badge>}
            {state === 'failed' && <Badge variant="destructive">{t('smartPlugImport.failedTitle')}</Badge>}
          </div>

          {state === 'processing' && (
            <p className="text-muted-foreground text-sm">
              {queued ? t('smartPlugImport.queuedNote') : t('smartPlugImport.asyncNote')}
            </p>
          )}

          {state === 'flaggedForReview' && (
            <div className="flex items-center gap-2.5 rounded-[12px] border border-status-trending/25 bg-status-trending/10 p-3">
              <span className="flex h-8 w-8 shrink-0 items-center justify-center rounded-[10px] border border-status-trending/30 bg-status-trending/15 text-status-trending">
                <TriangleAlert className="h-4 w-4" aria-hidden="true" />
              </span>
              <div className="min-w-0">
                <div className="text-sm font-semibold">{t('smartPlugImport.flaggedForReviewTitle')}</div>
                {fileName && <div className="text-muted-foreground truncate text-xs">{fileName}</div>}
              </div>
            </div>
          )}

          {(state === 'completed' || state === 'flaggedForReview') && gaps.length > 0 && (
            <div className="flex flex-col gap-2">
              {gaps.map((gap) => (
                <GapCard key={`${gap.startDate}-${gap.endDate}`} gap={gap} />
              ))}
            </div>
          )}

          {(state === 'completed' || state === 'awaitingMapping' || state === 'flaggedForReview' || state === 'failed') && (
            <Button type="button" variant="outline" size="sm" onClick={handleReset}>
              {t('smartPlugImport.uploadAnother')}
            </Button>
          )}
        </div>
      )}

      {error && <p className="text-destructive text-sm">{error}</p>}

      {state === 'awaitingMapping' && smartPlugImportIdRef.current && (
        <PowerPointMappingDialog
          smartPlugImportId={smartPlugImportIdRef.current}
          deviceTag={deviceTagRef.current}
          onMapped={handleMapped}
          onCancel={handleReset}
        />
      )}
    </GlassCard>
  )
}
