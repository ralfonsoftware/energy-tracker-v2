import type { ReactNode } from 'react'
import { useTranslation } from 'react-i18next'
import { GlassCard } from '@/components/ui/glass-card'
import { Badge } from '@/components/ui/badge'
import { Skeleton } from '@/components/ui/skeleton'
import { cn } from '@/lib/utils'
import { computeStatusDifference } from '@/lib/status-difference'
import type { StatusDto, StatusValue } from '@/lib/status-api'

interface StatusCardProps {
  status: StatusDto | null
  loading: boolean
  locale: string
  // Whether this render should play the entrance/specular-sweep animation (AC #6). Decided by
  // the caller (App.tsx), not derived internally via a remount key — DashboardPage/StatusCard
  // are fully unmounted and remounted whenever the user navigates to Settings and back, so an
  // internal key-based "did the fingerprint change" check can't distinguish "real recompute"
  // from "remounted after an unrelated navigation": there's no previous fiber left to diff
  // against. The caller tracks the last-animated Status fingerprint in a ref that survives the
  // navigation and passes the answer down as a plain boolean.
  playEntranceAnimation: boolean
  // Rendered inline inside the onboarding empty state only (mockups/key-dashboard.html's
  // empty frame) — the populated state's own primary button lives outside the card
  // (DashboardPage, Task 6/8), matching the mockup's real-screen layout.
  emptyStateAction?: ReactNode
  // The Story 2.7 "How was this calculated?" details-view trigger — rendered only in the
  // populated branch below (never loading/empty), a compositional slot exactly like
  // emptyStateAction so StatusCard stays a pure function of its props.
  detailTrigger?: ReactNode
}

// {typography.status-headline} — verbatim per-state copy from mockups/key-dashboard.html /
// direction-green-eco.html, the confirmed key-screen reference (DESIGN.md Components -> Status
// card). Kept fixed per state regardless of the pace/baseline delta's magnitude.
const HEADLINE_KEY: Record<StatusValue, string> = {
  withinRange: 'dashboard.status.headline.withinRange',
  belowBaseline: 'dashboard.status.headline.belowBaseline',
  trending: 'dashboard.status.headline.trending',
}

const BADGE_LABEL_KEY: Record<StatusValue, string> = {
  withinRange: 'dashboard.status.badge.withinRange',
  belowBaseline: 'dashboard.status.badge.belowBaseline',
  trending: 'dashboard.status.badge.trending',
}

// Raw status-triad token (dot only — large solid fill, no small-text AA contrast problem).
const DOT_CLASS: Record<StatusValue, string> = {
  withinRange: 'bg-status-within-range',
  belowBaseline: 'bg-status-below-baseline',
  trending: 'bg-status-trending',
}

// Dedicated AA-verified badge-bg/-text pair — never the raw triad above, which fails 2.85-3.98:1
// as small badge-label text against its own -bg tint (DESIGN.md Components -> Status card).
const BADGE_CLASS: Record<StatusValue, string> = {
  withinRange: 'bg-status-within-range-badge-bg text-status-within-range-badge-text',
  belowBaseline: 'bg-status-below-baseline-badge-bg text-status-below-baseline-badge-text',
  trending: 'bg-status-trending-badge-bg text-status-trending-badge-text',
}

export function StatusCard({ status, loading, locale, playEntranceAnimation, emptyStateAction, detailTrigger }: StatusCardProps) {
  const { t } = useTranslation()

  if (loading) {
    // Same GlassCard size="lg" footprint, and the same centered title/body/CTA shape as the
    // onboarding empty state below — not the populated state's dot+badge-row layout. A cold
    // load is most often a first-time household (the empty state is what resolves), so this
    // shape is the one least likely to reflow on resolution (AC #8); an existing household's
    // populated card differs only by gaining a dot+badge row and left-aligning text, a smaller
    // visual delta than the empty state would have had against the old dot+badge skeleton.
    return (
      <GlassCard size="lg" data-testid="status-card-skeleton" className="flex flex-col items-center gap-3 py-6 text-center">
        <Skeleton className="h-6 w-32" />
        <Skeleton className="h-4 w-48" />
        <Skeleton className="h-9 w-36 rounded-full" />
      </GlassCard>
    )
  }

  if (!status) {
    // FR-7 onboarding empty state — never blank space, never a default Status value. Rendered
    // inside the same GlassCard size="lg" footprint as the populated/loading states, per
    // mockups/key-dashboard.html's empty frame.
    return (
      <GlassCard size="lg" className="flex flex-col items-center gap-3 py-6 text-center">
        <p className="text-lg font-bold tracking-[-0.2px]">{t('dashboard.status.emptyTitle')}</p>
        <p className="max-w-[220px] text-sm text-muted-foreground">{t('dashboard.status.emptyBody')}</p>
        {emptyStateAction}
      </GlassCard>
    )
  }

  const numberFormat = new Intl.NumberFormat(locale, { maximumFractionDigits: 0 })
  const { sign, roundedMagnitude } = computeStatusDifference(status.paceToDateKwh, status.baselineToDateKwh)

  let supportingSentence: string
  if (sign === 'on') {
    supportingSentence = t('dashboard.status.body.onPace')
  } else if (sign === 'under') {
    supportingSentence = t('dashboard.status.body.underPace', { kwh: numberFormat.format(roundedMagnitude) })
  } else {
    supportingSentence = t('dashboard.status.body.overPace', { kwh: numberFormat.format(roundedMagnitude) })
  }

  return (
    <div
      className={cn('relative', playEntranceAnimation && 'motion-safe:animate-status-card-entrance')}
      style={playEntranceAnimation ? undefined : { opacity: 1, transform: 'none' }}
    >
      <GlassCard size="lg" className="relative overflow-hidden">
        <div
          aria-hidden="true"
          className={cn(
            'pointer-events-none absolute inset-0 opacity-0',
            playEntranceAnimation && 'motion-safe:animate-status-card-specular-sweep'
          )}
          style={{ backgroundImage: 'var(--status-card-specular-overlay)' }}
        />
        <div className="relative z-10 mb-4 flex items-center gap-2">
          <span aria-hidden="true" className={cn('size-2.5 rounded-full', DOT_CLASS[status.status])} />
          <Badge
            variant="outline"
            className={cn(
              'rounded-full border-0 px-[11px] py-1 text-[11px] font-bold tracking-[1.1px] uppercase',
              BADGE_CLASS[status.status]
            )}
          >
            {t(BADGE_LABEL_KEY[status.status])}
          </Badge>
        </div>
        {/* Stays mounted across re-renders (no remount key) so a screen reader sees this as a
            persistent aria-live region whose text content changes, rather than a freshly-inserted
            node with content already set — the latter is unreliably announced across AT (AC #7). */}
        <div aria-live="polite" className="relative z-10">
          <p className="mb-2 text-2xl font-bold tracking-[-0.3px]">{t(HEADLINE_KEY[status.status])}</p>
          <p className="text-sm tabular-nums text-muted-foreground">{supportingSentence}</p>
          {status.isLowConfidence && (
            <p className="mt-2 text-xs text-muted-foreground/80">{t('dashboard.status.lowConfidenceNote')}</p>
          )}
          {detailTrigger}
        </div>
      </GlassCard>
    </div>
  )
}
