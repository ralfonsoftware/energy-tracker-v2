import { useEffect, useState, type ReactNode } from 'react'
import { useTranslation } from 'react-i18next'
import { Button } from '@/components/ui/button'
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
  DialogTrigger,
} from '@/components/ui/dialog'
import { Skeleton } from '@/components/ui/skeleton'
import { GLASS_MODAL_CLASSNAME } from '@/lib/glass-classnames'
import { computeStatusDifference } from '@/lib/status-difference'
import { fetchStatusDetail, type StatusDetailDto } from '@/lib/status-api'

interface StatusDetailDialogProps {
  trigger: ReactNode
  open: boolean
  onOpenChange: (open: boolean) => void
  locale: string
}

// Read-only drill-down for the aggregate figures behind the Status card's headline (AC #1, #2,
// #4) — no chart, no Meter Reading list, just labeled figures. No mockup exists for this story
// (added post-UX-freeze) — reuses the established Dialog + GLASS_MODAL_CLASSNAME shell rather
// than inventing new visual language (Story 2.6's precedent).
export function StatusDetailDialog({ trigger, open, onOpenChange, locale }: StatusDetailDialogProps) {
  const { t } = useTranslation()
  const [detail, setDetail] = useState<StatusDetailDto | null>(null)
  const [loading, setLoading] = useState(false)
  const [error, setError] = useState(false)

  // Fetch-on-open, reset-on-close: a re-open always shows a fresh fetch, never stale data left
  // over from a previous open.
  useEffect(() => {
    if (!open) {
      setDetail(null)
      setLoading(false)
      setError(false)
      return
    }

    let cancelled = false
    setLoading(true)
    setError(false)
    fetchStatusDetail()
      .then((result) => {
        if (cancelled) {
          return
        }
        setDetail(result)
      })
      .catch(() => {
        if (cancelled) {
          return
        }
        setError(true)
      })
      .finally(() => {
        if (!cancelled) {
          setLoading(false)
        }
      })

    return () => {
      cancelled = true
    }
  }, [open])

  const numberFormat = new Intl.NumberFormat(locale, { maximumFractionDigits: 0 })

  let differenceSentence: string | null = null
  if (detail) {
    const { sign, roundedMagnitude } = computeStatusDifference(detail.paceToDateKwh, detail.baselineToDateKwh)
    if (sign === 'on') {
      differenceSentence = t('dashboard.status.body.onPace')
    } else if (sign === 'under') {
      differenceSentence = t('dashboard.status.body.underPace', { kwh: numberFormat.format(roundedMagnitude) })
    } else {
      differenceSentence = t('dashboard.status.body.overPace', { kwh: numberFormat.format(roundedMagnitude) })
    }
  }

  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      <DialogTrigger asChild>{trigger}</DialogTrigger>
      <DialogContent className={GLASS_MODAL_CLASSNAME}>
        <DialogHeader>
          <DialogTitle>{t('dashboard.statusDetail.title')}</DialogTitle>
          <DialogDescription className="sr-only">{t('dashboard.statusDetail.trigger')}</DialogDescription>
        </DialogHeader>

        {/* Stays mounted across re-renders (no remount key) so a screen reader sees this as a
            persistent aria-live region whose text content changes, rather than a freshly-inserted
            node with content already set — mirrors status-card.tsx's identical loading/loaded
            content-swap discipline. */}
        <div aria-live="polite">
          {loading && (
            <div className="flex flex-col gap-3">
              <Skeleton className="h-5 w-full" />
              <Skeleton className="h-5 w-full" />
              <Skeleton className="h-5 w-full" />
              <Skeleton className="h-5 w-full" />
            </div>
          )}

          {!loading && error && <p className="text-destructive text-sm">{t('dashboard.statusDetail.loadError')}</p>}

          {!loading && !error && detail && (
            <div className="flex flex-col gap-3">
              <div className="flex items-baseline justify-between gap-4">
                <span className="text-muted-foreground text-sm">{t('dashboard.statusDetail.paceLabel')}</span>
                <span className="text-sm font-semibold tabular-nums">{numberFormat.format(detail.paceToDateKwh)} kWh</span>
              </div>
              <div className="flex items-baseline justify-between gap-4">
                <span className="text-muted-foreground text-sm">{t('dashboard.statusDetail.baselineLabel')}</span>
                <span className="text-sm font-semibold tabular-nums">
                  {numberFormat.format(detail.baselineToDateKwh)} kWh
                  <span className="text-muted-foreground ml-1 font-normal">
                    {/* Floor, not round: "over X days" must stay literally true — rounding up would
                        overstate elapsed time that hasn't actually passed yet. */}
                    ({t('dashboard.statusDetail.elapsedDays', { days: numberFormat.format(Math.floor(detail.elapsedDays)) })})
                  </span>
                </span>
              </div>
              <div className="flex items-baseline justify-between gap-4">
                <span className="text-muted-foreground text-sm">{t('dashboard.statusDetail.differenceLabel')}</span>
                <span className="text-sm font-semibold tabular-nums">{differenceSentence}</span>
              </div>
              <div className="flex items-baseline justify-between gap-4">
                <span className="text-muted-foreground text-sm">{t('dashboard.statusDetail.thresholdLabel')}</span>
                <span className="text-sm font-semibold tabular-nums">{numberFormat.format(detail.trendingThresholdKwh)} kWh</span>
              </div>

              {detail.isLowConfidence && (
                <p className="text-muted-foreground/80 mt-1 text-xs">
                  {t('dashboard.statusDetail.lowConfidenceExplanation', {
                    // Ceil, not round: isLowConfidence only fires when the raw value is strictly
                    // greater than the (integer) threshold, so ceiling is the only rounding that
                    // can never display a day count equal to or below the threshold it's paired with.
                    days: numberFormat.format(Math.ceil(detail.daysSinceLastReading)),
                    threshold: numberFormat.format(detail.lowConfidenceGapDaysThreshold),
                  })}
                </p>
              )}
            </div>
          )}
        </div>

        <DialogFooter>
          <Button type="button" variant="outline" onClick={() => onOpenChange(false)}>
            {t('dashboard.statusDetail.close')}
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  )
}
