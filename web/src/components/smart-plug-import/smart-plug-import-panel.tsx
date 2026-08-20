import { useEffect, useRef, useState } from 'react'
import { useTranslation } from 'react-i18next'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { GlassCard } from '@/components/ui/glass-card'
import { ApiError, fetchJobStatus, uploadSmartPlugFile } from '@/lib/smart-plug-import-api'

type ImportState = 'idle' | 'uploading' | 'processing' | 'completed' | 'awaitingMapping' | 'failed'

const POLL_INTERVAL_MS = 2000
// Tolerate a few consecutive transient network blips while polling before giving up — a single
// dropped fetch doesn't mean the backend job itself failed.
const MAX_CONSECUTIVE_POLL_FAILURES = 3

// Mockup reference: key-smart-plug-import.html State 1 ("Uploading, non-blocking") — the
// dropzone, file-choose control, processing pill, and async-note copy. States 2-5 (gap
// summaries, create/map prompt) are Story 3.2/3.3's scope, not this component's. Colors are
// deliberately plain shadcn Badge variants, not the mockup's own status-triad colors — the
// UX rubric review flagged that reuse as a DESIGN.md violation (non-status badge borrowing
// Status semantic colors).
export function SmartPlugImportPanel() {
  const { t } = useTranslation()
  const [state, setState] = useState<ImportState>('idle')
  const [fileName, setFileName] = useState<string | null>(null)
  const [error, setError] = useState<string | null>(null)
  const fileInputRef = useRef<HTMLInputElement>(null)
  const jobIdRef = useRef<string | null>(null)

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
        if (job.status === 'completed') {
          setState(job.importStatus === 'awaitingpowerpointmapping' ? 'awaitingMapping' : 'completed')
        } else if (job.status === 'failed') {
          setError(job.errorMessage ?? t('smartPlugImport.errorGeneric'))
          setState('failed')
        }
      } catch {
        if (cancelled) {
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
    jobIdRef.current = null
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

          {state === 'processing' && <p className="text-muted-foreground text-sm">{t('smartPlugImport.asyncNote')}</p>}
          {state === 'awaitingMapping' && <p className="text-muted-foreground text-sm">{t('smartPlugImport.awaitingMappingNote')}</p>}

          {(state === 'completed' || state === 'awaitingMapping' || state === 'failed') && (
            <Button type="button" variant="outline" size="sm" onClick={handleReset}>
              {t('smartPlugImport.uploadAnother')}
            </Button>
          )}
        </div>
      )}

      {error && <p className="text-destructive text-sm">{error}</p>}
    </GlassCard>
  )
}
