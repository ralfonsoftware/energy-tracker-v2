import { useTranslation } from 'react-i18next'
import { Badge } from '@/components/ui/badge'
import type { SmartPlugImportGapDto } from '@/lib/smart-plug-import-api'

// Mockup reference: key-smart-plug-import.html States 2/4/5 (lines 361-539) — one component
// reused across all three SmartPlugImportGapTreatment values. Reuses only the existing
// {colors.status-trending} DESIGN.md token (via its AA-verified badge-bg/-text pair, and a low
// opacity of the raw color for the card tint) for `estimated`/`flaggedforreview` — no new
// "flagged data" color, per AC #8. `missing` gets a visually distinct-but-not-destructive neutral
// treatment (mockup's `.gap-badge.missing`), since that gap was never filled at all.
export function GapCard({ gap }: { gap: SmartPlugImportGapDto }) {
  const { t, i18n } = useTranslation()

  const formatDate = (iso: string) =>
    new Intl.DateTimeFormat(i18n.language, { dateStyle: 'medium', timeZone: 'UTC' }).format(new Date(iso))

  const dayCount = Math.round((new Date(gap.endDate).getTime() - new Date(gap.startDate).getTime()) / 86_400_000) + 1
  const dateRange = gap.startDate === gap.endDate ? formatDate(gap.startDate) : `${formatDate(gap.startDate)} – ${formatDate(gap.endDate)}`

  const isNeutral = gap.treatment === 'missing'
  const cardTint = isNeutral ? 'bg-muted/40 border-border' : 'bg-status-trending/10 border-status-trending/25'
  const badgeClassName = isNeutral
    ? 'bg-secondary text-secondary-foreground'
    : 'bg-status-trending-badge-bg text-status-trending-badge-text'

  const badgeLabel =
    gap.treatment === 'estimated'
      ? t('smartPlugImport.gaps.estimatedBadge')
      : gap.treatment === 'missing'
        ? t('smartPlugImport.gaps.missingBadge')
        : t('smartPlugImport.gaps.flaggedForReviewBadge')

  const detail =
    gap.treatment === 'estimated'
      ? t('smartPlugImport.gaps.estimatedDetail', {
          days: dayCount,
          dailyAverage: gap.estimatedTotalKwh !== null ? (gap.estimatedTotalKwh / dayCount).toFixed(1) : '0.0',
        })
      : gap.treatment === 'missing'
        ? t('smartPlugImport.gaps.missingDetail', { days: dayCount })
        : t('smartPlugImport.gaps.flaggedForReviewDetail')

  return (
    <div className={`flex flex-col gap-2 rounded-[12px] border p-3 ${cardTint}`}>
      <div className="flex items-center gap-2">
        <Badge variant="outline" className={badgeClassName}>
          {badgeLabel}
        </Badge>
        <span className="text-sm font-semibold">{dateRange}</span>
      </div>
      <p className="text-muted-foreground text-sm">{detail}</p>
    </div>
  )
}
