import { useCallback, useEffect, useState } from 'react'
import { useTranslation } from 'react-i18next'
import { Button } from '@/components/ui/button'
import { GlassCard } from '@/components/ui/glass-card'
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from '@/components/ui/table'
import { fetchEventHistory, messageForEventError, type EventDto, type EventHistoryPageDto } from '@/lib/event-api'

interface EventsCardProps {
  locale: string
}

const PAGE_SIZE = 20

// Modeled directly on MeterReadingsCard's idiom (GlassCard + collapsed details, role="heading"
// summary, PAGE_SIZE, locale date formatting, pagination) — all three Trend History cards share it.
export function EventsCard({ locale }: EventsCardProps) {
  const { t } = useTranslation()
  const [page, setPage] = useState(1)
  const [data, setData] = useState<EventHistoryPageDto | null>(null)
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState<string | null>(null)

  const load = useCallback(
    (targetPage: number) => {
      let cancelled = false
      setLoading(true)
      setError(null)
      fetchEventHistory(targetPage, PAGE_SIZE)
        .then((result) => {
          if (cancelled) {
            return
          }
          setData(result)
        })
        .catch((err: unknown) => {
          if (cancelled) {
            return
          }
          setError(messageForEventError(err, t))
        })
        .finally(() => {
          if (!cancelled) {
            setLoading(false)
          }
        })

      return () => {
        cancelled = true
      }
    },
    [t],
  )

  useEffect(() => load(page), [load, page])

  const dateTimeFormat = new Intl.DateTimeFormat(locale, { dateStyle: 'medium', timeStyle: 'short' })

  const totalPages = data ? Math.max(1, Math.ceil(data.totalCount / data.pageSize)) : 1

  return (
    <GlassCard className="pb-1">
      <details className="group">
        <summary className="flex cursor-pointer list-none items-center justify-between text-sm font-semibold [&::-webkit-details-marker]:hidden">
          <span role="heading" aria-level={2}>
            {t('trendHistory.eventsCard.summary', { count: data?.totalCount ?? 0 })}
          </span>
          <span className="text-muted-foreground text-xs font-bold">
            <span className="group-open:hidden">{t('trendHistory.eventsCard.expand')}</span>
            <span className="hidden group-open:inline">{t('trendHistory.eventsCard.collapse')}</span>
          </span>
        </summary>

        <div className="mt-3">
          <div aria-live="polite">
            {loading && <p className="text-muted-foreground text-sm">{t('trendHistory.eventsCard.loading')}</p>}

            {!loading && error && <p className="text-destructive text-sm">{error}</p>}

            {!loading && !error && data && data.totalCount === 0 && (
              <p className="text-muted-foreground text-sm">{t('trendHistory.eventsCard.emptyState')}</p>
            )}

            {!loading && !error && data && data.totalCount > 0 && (
              <Table>
                <TableHeader>
                  <TableRow>
                    <TableHead>{t('trendHistory.eventsCard.descriptionColumn')}</TableHead>
                    <TableHead>{t('trendHistory.eventsCard.timestampColumn')}</TableHead>
                  </TableRow>
                </TableHeader>
                <TableBody>
                  {data.items.map((item) => (
                    <EventRow key={item.id} item={item} dateTimeFormat={dateTimeFormat} />
                  ))}
                </TableBody>
              </Table>
            )}
          </div>

          {!error && data && data.totalCount > 0 && (
            <div className="flex items-center justify-center gap-4 pb-3">
              <Button variant="outline" disabled={loading || page <= 1} onClick={() => setPage((p) => p - 1)}>
                {t('trendHistory.eventsCard.previousPage')}
              </Button>
              <span className="text-muted-foreground text-sm">
                {t('trendHistory.eventsCard.pageIndicator', { page, totalPages })}
              </span>
              <Button variant="outline" disabled={loading || page >= totalPages} onClick={() => setPage((p) => p + 1)}>
                {t('trendHistory.eventsCard.nextPage')}
              </Button>
            </div>
          )}
        </div>
      </details>
    </GlassCard>
  )
}

// AD-10: taggedEntityName is rendered verbatim from the write-time snapshot. There is deliberately
// no fetch of /api/rooms, /api/power-points, or /api/devices here, and no branch on whether the
// tagged entity still exists — a tag whose target was later archived renders identically to one
// that's still live, with no "(deleted)" decoration.
function EventRow({ item, dateTimeFormat }: { item: EventDto; dateTimeFormat: Intl.DateTimeFormat }) {
  return (
    <TableRow>
      <TableCell>
        <div className="flex flex-col gap-1">
          <span>{item.description}</span>
          {item.taggedEntityName != null && (
            <span className="text-muted-foreground text-xs">{item.taggedEntityName}</span>
          )}
        </div>
      </TableCell>
      <TableCell>{dateTimeFormat.format(new Date(item.occurredAt))}</TableCell>
    </TableRow>
  )
}
