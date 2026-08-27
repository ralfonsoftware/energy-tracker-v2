import { useCallback, useEffect, useRef, useState } from 'react'
import { useTranslation } from 'react-i18next'
import { TriangleAlert } from 'lucide-react'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { GlassCard } from '@/components/ui/glass-card'
import { GapCard } from '@/components/smart-plug-import/gap-card'
import { PowerPointMappingDialog } from '@/components/smart-plug-import/power-point-mapping-dialog'
import { useSmartPlugImportJob } from '@/components/smart-plug-import/use-smart-plug-import-job'

interface SmartPlugImportPageProps {
  onBack: () => void
}

interface QueueEntry {
  id: string
  file: File
}

// The dedicated, shared Smart Plug Import screen (FR-4 amendment, AC #3): reached from the
// Dashboard's new icon entry point today, and — once Epic 4 builds Trend History — from a second
// icon there too, via the exact same `view === 'smartPlugImport'` destination. Mirrors
// MeterReadingHistoryPage's/SettingsPage's full-screen-with-topbar shape (no NavChrome — this
// surface has no bottom-tab slot, same as Meter Reading History).
export function SmartPlugImportPage({ onBack }: SmartPlugImportPageProps) {
  const { t } = useTranslation()
  const [queue, setQueue] = useState<QueueEntry[]>([])
  const fileInputRef = useRef<HTMLInputElement>(null)
  const headingRef = useRef<HTMLHeadingElement>(null)
  // Tracks which queue entries currently have an in-flight upload/parse, so the shared
  // `asyncNote` below the queue only claims background work is happening while that's actually
  // true — rather than staying pinned to "we're parsing this in the background" even after every
  // item has already completed, failed, or is waiting on the user's Power Point mapping choice.
  const [activeIds, setActiveIds] = useState<ReadonlySet<string>>(new Set())

  // Move focus to the page heading on mount — this is a first-class new navigation destination
  // (not a dialog/sub-panel with an existing focus story), so a keyboard/screen-reader user
  // arriving here via the Dashboard's icon button needs an explicit landing point.
  useEffect(() => {
    headingRef.current?.focus()
  }, [])

  const setEntryActive = useCallback((id: string, isActive: boolean) => {
    setActiveIds((current) => {
      if (current.has(id) === isActive) {
        return current
      }
      const next = new Set(current)
      if (isActive) {
        next.add(id)
      } else {
        next.delete(id)
      }
      return next
    })
  }, [])

  // Every selected/dropped file becomes its own queue entry immediately (AC #6) — each entry then
  // mounts its own SmartPlugImportQueueItem, which fires its own upload on mount. Several entries
  // added in one action therefore upload concurrently, never one-by-one (AC #4).
  const addFiles = (files: FileList) => {
    const entries = Array.from(files).map((file) => ({ id: crypto.randomUUID(), file }))
    if (entries.length > 0) {
      setQueue((current) => [...current, ...entries])
    }
  }

  const handleInputChange = (event: React.ChangeEvent<HTMLInputElement>) => {
    const files = event.target.files
    event.target.value = ''
    if (files) {
      addFiles(files)
    }
  }

  const handleDrop = (event: React.DragEvent<HTMLDivElement>) => {
    event.preventDefault()
    addFiles(event.dataTransfer.files)
  }

  const removeEntry = (id: string) => {
    setQueue((current) => current.filter((entry) => entry.id !== id))
    setEntryActive(id, false)
  }

  return (
    <main className="flex min-h-svh flex-col gap-6 p-4">
      <div className="flex items-center justify-between">
        <h1 ref={headingRef} tabIndex={-1} className="text-2xl font-semibold outline-none">
          {t('smartPlugImport.heading')}
        </h1>
        <Button variant="outline" onClick={onBack}>
          {t('smartPlugImport.backToApp')}
        </Button>
      </div>

      <GlassCard className="flex w-full flex-col gap-2">
        <div
          data-testid="smart-plug-import-dropzone"
          className="flex flex-col items-center gap-2 rounded-[12px] border border-dashed border-[rgba(40,70,50,0.2)] p-4 text-center dark:border-[rgba(210,235,220,0.2)]"
          onDragOver={(event) => event.preventDefault()}
          onDrop={handleDrop}
        >
          <p className="text-sm">{queue.length > 0 ? t('smartPlugImport.addMoreFiles') : t('smartPlugImport.dropHint')}</p>
          <p className="text-muted-foreground text-xs">{t('smartPlugImport.formats')}</p>
          <Button type="button" variant="outline" size="sm" onClick={() => fileInputRef.current?.click()}>
            {t('smartPlugImport.chooseFile')}
          </Button>
          <input
            ref={fileInputRef}
            type="file"
            accept=".xlsx,.csv"
            multiple
            className="sr-only"
            onChange={handleInputChange}
            aria-label={t('smartPlugImport.chooseFile')}
          />
        </div>
      </GlassCard>

      {queue.length > 0 && (
        <div className="flex flex-col gap-3">
          {queue.map((entry) => (
            <SmartPlugImportQueueItem
              key={entry.id}
              id={entry.id}
              file={entry.file}
              onRemove={() => removeEntry(entry.id)}
              onActiveChange={setEntryActive}
            />
          ))}
          {activeIds.size > 0 && <p className="text-muted-foreground text-sm">{t('smartPlugImport.asyncNote')}</p>}
        </div>
      )}
    </main>
  )
}

// Mockup reference: key-smart-plug-import.html Frame 6 (`.queue-file`) for the row shape, State 3
// (PowerPointMappingDialog) and States 2/4/5 (GapCard) for everything below the badge — all
// reused unmodified per queue item, per Story 3.1/3.3's own "don't copy the mockup's literal
// status-triad colors" precedent. `Waiting` vs `Processing` reuses the existing 404-while-polling
// idiom (`queued`) client-side-only, per this story's own Dev Notes — no new backend state.
function SmartPlugImportQueueItem({
  id,
  file,
  onRemove,
  onActiveChange,
}: {
  id: string
  file: File
  onRemove: () => void
  onActiveChange: (id: string, isActive: boolean) => void
}) {
  const { t } = useTranslation()
  const job = useSmartPlugImportJob(file)
  const isActive = job.state === 'uploading' || job.state === 'processing'

  // Reports this item's uploading/processing status up to the parent so the shared asyncNote
  // reflects the batch's real state; always clears its own contribution on cleanup (transition
  // away from active, or a genuine unmount via `onRemove`) so a removed/finished item never keeps
  // the shared note pinned on. `onActiveChange` must be the parent's stable (useCallback'd)
  // dispatcher passed straight through, not a new arrow function per render — otherwise this
  // effect's cleanup+setup would refire on every parent render, toggling `activeIds` off and back
  // on and causing a render loop.
  useEffect(() => {
    onActiveChange(id, isActive)
    return () => onActiveChange(id, false)
  }, [id, isActive, onActiveChange])

  const dismissable =
    job.state === 'completed' || job.state === 'flaggedForReview' || job.state === 'failed'

  return (
    <GlassCard className="flex w-full flex-col gap-2">
      <div className="flex flex-col gap-2" aria-live="polite">
        <div className="flex items-center justify-between gap-2">
          <span className="truncate text-sm">{file.name}</span>
          {job.state === 'uploading' && <Badge variant="outline">{t('smartPlugImport.uploading')}</Badge>}
          {job.state === 'processing' && (
            <Badge variant="outline">
              {job.queued ? t('smartPlugImport.waitingBadge') : t('smartPlugImport.processingBadge')}
            </Badge>
          )}
          {job.state === 'completed' && <Badge variant="secondary">{t('smartPlugImport.completeTitle')}</Badge>}
          {job.state === 'awaitingMapping' && <Badge variant="outline">{t('smartPlugImport.awaitingMappingTitle')}</Badge>}
          {job.state === 'failed' && <Badge variant="destructive">{t('smartPlugImport.failedTitle')}</Badge>}
        </div>

        {job.state === 'processing' && job.queued && (
          <p className="text-muted-foreground text-xs">{t('smartPlugImport.queuedNote')}</p>
        )}

        {job.state === 'flaggedForReview' && (
          <div className="flex items-center gap-2.5 rounded-[12px] border border-status-trending/25 bg-status-trending/10 p-3">
            <span className="flex h-8 w-8 shrink-0 items-center justify-center rounded-[10px] border border-status-trending/30 bg-status-trending/15 text-status-trending">
              <TriangleAlert className="h-4 w-4" aria-hidden="true" />
            </span>
            <div className="min-w-0">
              <div className="text-sm font-semibold">{t('smartPlugImport.flaggedForReviewTitle')}</div>
              <div className="text-muted-foreground truncate text-xs">{file.name}</div>
            </div>
          </div>
        )}

        {(job.state === 'completed' || job.state === 'flaggedForReview') && job.gaps.length > 0 && (
          <div className="flex flex-col gap-2">
            {job.gaps.map((gap) => (
              <GapCard key={`${gap.startDate}-${gap.endDate}`} gap={gap} />
            ))}
          </div>
        )}

        {job.error && <p className="text-destructive text-sm">{job.error}</p>}

        {dismissable && (
          <Button type="button" variant="outline" size="sm" onClick={onRemove}>
            {t('smartPlugImport.removeFromQueue')}
          </Button>
        )}
      </div>

      {job.state === 'awaitingMapping' && job.smartPlugImportId && (
        <PowerPointMappingDialog
          smartPlugImportId={job.smartPlugImportId}
          deviceTag={job.deviceTag}
          onMapped={job.refreshAfterMapping}
          onCancel={onRemove}
        />
      )}
    </GlassCard>
  )
}
