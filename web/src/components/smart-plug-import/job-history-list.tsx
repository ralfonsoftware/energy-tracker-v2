import { useCallback, useEffect, useRef, useState } from 'react'
import { useTranslation } from 'react-i18next'
import { ChevronRight, CircleCheck, CircleX, Clock, Flag, LoaderCircle, Plug } from 'lucide-react'
import { Badge } from '@/components/ui/badge'
import { GlassCard } from '@/components/ui/glass-card'
import { GapCard } from '@/components/smart-plug-import/gap-card'
import { PowerPointMappingDialog } from '@/components/smart-plug-import/power-point-mapping-dialog'
import { formatRelativeTime } from '@/lib/format-relative-time'
import { fetchSmartPlugImportJobs, type SmartPlugImportJobDto, type SmartPlugImportJobStateValue } from '@/lib/smart-plug-import-api'

// Distinct from, and slower than, useSmartPlugImportJob's own 2s per-item poll — no story/
// architecture doc pins an exact number, an implementation default (Task 5's own Dev Notes).
const POLL_INTERVAL_MS = 8000

const STATE_ICON: Record<SmartPlugImportJobStateValue, typeof Clock> = {
  waiting: Clock,
  processing: LoaderCircle,
  success: CircleCheck,
  error: CircleX,
  needsMapping: Plug,
  flaggedForReview: Flag,
}

// Story 3.1/3.3's own "don't copy the mockup's literal hex colors" precedent — reuses this
// codebase's actual token/variant discipline instead. Waiting/Success are neutral (no chrome);
// Processing/Needs Mapping reuse the exact nav-chrome-active tokens Story 3.5's entry-icon button
// already established; Error is the shadcn `destructive` variant; Flagged for Review reuses
// GapCard.tsx's status-trending badge tokens (never Error's red — this state is "uncertain", not
// a failure).
function badgeVariant(state: SmartPlugImportJobStateValue): 'outline' | 'secondary' | 'destructive' {
  if (state === 'error') return 'destructive'
  if (state === 'success') return 'secondary'
  return 'outline'
}

function badgeClassName(state: SmartPlugImportJobStateValue): string | undefined {
  if (state === 'processing' || state === 'needsMapping') {
    return 'bg-nav-chrome-active-bg text-nav-chrome-active-foreground'
  }
  if (state === 'flaggedForReview') {
    return 'bg-status-trending-badge-bg text-status-trending-badge-text'
  }
  return undefined
}

// The household-wide Job Status & History list (Story 3.6/FR-32) — every import job any member
// has ever queued, rendered below the per-session upload queue on the same screen (EXPERIENCE.md:
// "sits on the same screen, below the upload area"), never a second route/frame.
export function JobHistoryList() {
  const { t, i18n } = useTranslation()
  const [jobs, setJobs] = useState<SmartPlugImportJobDto[] | null>(null)
  const [expandedJobId, setExpandedJobId] = useState<string | null>(null)
  const [mappingJob, setMappingJob] = useState<SmartPlugImportJobDto | null>(null)
  // Guards every async state update below against a fetch resolving after unmount — same
  // mountedRef discipline PowerPointMappingDialog already establishes for this exact class of
  // race (a slow request outliving the component, e.g. navigating away mid-poll).
  const mountedRef = useRef(true)
  useEffect(() => {
    mountedRef.current = true
    return () => {
      mountedRef.current = false
    }
  }, [])

  const load = useCallback(() => {
    fetchSmartPlugImportJobs()
      .then((data) => {
        if (mountedRef.current) {
          setJobs(data)
        }
      })
      .catch(() => {
        // Best-effort background refresh — a transient failure leaves the last-known list
        // rendered rather than clearing it or showing an error, same tolerant discipline as
        // useSmartPlugImportJob's own polling.
      })
  }, [])

  useEffect(() => {
    load()
    const intervalId = window.setInterval(load, POLL_INTERVAL_MS)
    return () => window.clearInterval(intervalId)
  }, [load])

  const badgeLabel = (state: SmartPlugImportJobStateValue) => {
    if (state === 'waiting') return t('smartPlugImport.waitingBadge')
    if (state === 'processing') return t('smartPlugImport.processingBadge')
    return t(`smartPlugImport.jobHistory.badges.${state}`)
  }

  if (jobs === null) {
    return null
  }

  return (
    <div className="flex flex-col gap-3">
      <p className="text-sm font-semibold">{t('smartPlugImport.jobHistory.listLabel')}</p>

      {jobs.length === 0 ? (
        <GlassCard className="flex flex-col items-center gap-2 py-10 text-center">
          <p className="text-sm font-semibold">{t('smartPlugImport.jobHistory.emptyTitle')}</p>
          <p className="text-muted-foreground max-w-xs text-xs">{t('smartPlugImport.jobHistory.emptyBody')}</p>
        </GlassCard>
      ) : (
        <GlassCard className="flex flex-col">
          {jobs.map((job) => {
            const Icon = STATE_ICON[job.state]
            const displayName = job.queuedByDisplayName ?? t('smartPlugImport.jobHistory.queuedByFallback')
            const metaLine = `${t('smartPlugImport.jobHistory.queuedBy', { member: displayName })} · ${formatRelativeTime(job.queuedAtUtc, i18n.language)}${
              job.state === 'error' && job.errorMessage ? ` · ${job.errorMessage}` : ''
            }`

            const rowContent = (
              <>
                <span className="flex size-8 shrink-0 items-center justify-center rounded-lg border border-border/60">
                  <Icon className={`size-4${job.state === 'processing' ? ' animate-spin' : ''}`} aria-hidden="true" />
                </span>
                <span className="min-w-0 flex-1">
                  <span className="block truncate text-sm font-semibold">
                    {job.fileName ?? t('smartPlugImport.jobHistory.unknownFile')}
                  </span>
                  <span className="text-muted-foreground block truncate text-xs">{metaLine}</span>
                </span>
                <Badge variant={badgeVariant(job.state)} className={badgeClassName(job.state)}>
                  {badgeLabel(job.state)}
                </Badge>
              </>
            )

            const fileLabel = job.fileName ?? t('smartPlugImport.jobHistory.unknownFile')

            return (
              <div key={job.jobId} className="flex flex-col border-b border-border/50 last:border-b-0">
                {job.state === 'needsMapping' ? (
                  <button
                    type="button"
                    onClick={() => setMappingJob(job)}
                    aria-label={t('smartPlugImport.jobHistory.mapAriaLabel', { fileName: fileLabel })}
                    className="flex items-center gap-3 py-3 text-left"
                  >
                    {rowContent}
                    <ChevronRight className="text-muted-foreground size-4 shrink-0" aria-hidden="true" />
                  </button>
                ) : job.state === 'flaggedForReview' ? (
                  <button
                    type="button"
                    onClick={() => setExpandedJobId((current) => (current === job.jobId ? null : job.jobId))}
                    aria-expanded={expandedJobId === job.jobId}
                    aria-label={t('smartPlugImport.jobHistory.reviewAriaLabel', { fileName: fileLabel })}
                    className="flex items-center gap-3 py-3 text-left"
                  >
                    {rowContent}
                    <ChevronRight className="text-muted-foreground size-4 shrink-0" aria-hidden="true" />
                  </button>
                ) : (
                  <div className="flex items-center gap-3 py-3">{rowContent}</div>
                )}

                {job.state === 'flaggedForReview' && expandedJobId === job.jobId && job.gaps.length > 0 && (
                  <div className="flex flex-col gap-2 pb-3">
                    {job.gaps.map((gap) => (
                      <GapCard key={`${gap.startDate}-${gap.endDate}`} gap={gap} />
                    ))}
                  </div>
                )}
              </div>
            )
          })}
        </GlassCard>
      )}

      {mappingJob && (
        <PowerPointMappingDialog
          smartPlugImportId={mappingJob.smartPlugImportId ?? ''}
          deviceTag={mappingJob.deviceTag ?? ''}
          onMapped={() => {
            setMappingJob(null)
            load()
          }}
          onCancel={() => setMappingJob(null)}
        />
      )}
    </div>
  )
}
