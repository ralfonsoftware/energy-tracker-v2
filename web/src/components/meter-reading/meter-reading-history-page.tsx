import { useCallback, useEffect, useState } from 'react'
import { useTranslation } from 'react-i18next'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from '@/components/ui/table'
import { fetchMeterReadingHistory, type MeterReadingHistoryItemDto, type MeterReadingHistoryPageDto } from '@/lib/meter-reading-history-api'
import { EditMeterReadingDialog } from './edit-meter-reading-dialog'

interface MeterReadingHistoryPageProps {
  locale: string
  onBack: () => void
}

const PAGE_SIZE = 20

// A dedicated full page (SettingsPage's shell), not a dialog — a paginated, editable list doesn't
// fit a dialog well (confirmed with Ralf during story creation, see the story's own header
// section). No NavChrome here — this surface has no tab slot; nav-chrome.tsx's 4 fixed entries are
// all already claimed (see Task 12's Dashboard trigger, the only way in).
export function MeterReadingHistoryPage({ locale, onBack }: MeterReadingHistoryPageProps) {
  const { t } = useTranslation()
  const [page, setPage] = useState(1)
  const [data, setData] = useState<MeterReadingHistoryPageDto | null>(null)
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState(false)
  const [editing, setEditing] = useState<MeterReadingHistoryItemDto | null>(null)

  const load = useCallback((targetPage: number) => {
    let cancelled = false
    setLoading(true)
    setError(false)
    fetchMeterReadingHistory(targetPage, PAGE_SIZE)
      .then((result) => {
        if (cancelled) {
          return
        }
        setData(result)
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
  }, [])

  useEffect(() => load(page), [load, page])

  const numberFormat = new Intl.NumberFormat(locale, { maximumFractionDigits: 2 })
  const dateTimeFormat = new Intl.DateTimeFormat(locale, { dateStyle: 'medium', timeStyle: 'short' })

  const totalPages = data ? Math.max(1, Math.ceil(data.totalCount / data.pageSize)) : 1

  return (
    <main className="flex min-h-svh flex-col gap-6 p-4">
      <div className="flex items-center justify-between">
        <h1 className="text-2xl font-semibold">{t('meterReadingHistory.heading')}</h1>
        <Button variant="outline" onClick={onBack}>
          {t('meterReadingHistory.backToApp')}
        </Button>
      </div>

      <div aria-live="polite">
        {loading && <p className="text-muted-foreground text-sm">{t('meterReadingHistory.loading')}</p>}

        {!loading && error && <p className="text-destructive text-sm">{t('meterReadingHistory.loadError')}</p>}

        {!loading && !error && data && data.totalCount === 0 && (
          <p className="text-muted-foreground text-sm">{t('meterReadingHistory.emptyState')}</p>
        )}

        {!loading && !error && data && data.totalCount > 0 && (
          <Table>
            <TableHeader>
              <TableRow>
                <TableHead>{t('meterReadingHistory.valueColumn')}</TableHead>
                <TableHead>{t('meterReadingHistory.timestampColumn')}</TableHead>
                <TableHead />
              </TableRow>
            </TableHeader>
            <TableBody>
              {data.items.map((item) => (
                <TableRow key={item.id}>
                  <TableCell>
                    <div className="flex flex-col gap-1">
                      <div className="flex items-center gap-2">
                        <span className="font-semibold tabular-nums">{numberFormat.format(item.kwhValue)} kWh</span>
                        {item.isPendingRegression && (
                          <Badge variant="outline">{t('meterReadingHistory.pendingBadge')}</Badge>
                        )}
                      </div>
                      {item.correctedFromKwhValue !== null && (
                        <span className="text-muted-foreground text-xs">
                          {t('meterReadingHistory.correctedFrom', { kwh: numberFormat.format(item.correctedFromKwhValue) })}
                        </span>
                      )}
                    </div>
                  </TableCell>
                  <TableCell>{dateTimeFormat.format(new Date(item.readingTimestamp))}</TableCell>
                  <TableCell>
                    <Button
                      variant="outline"
                      size="sm"
                      onClick={() => setEditing(item)}
                      aria-label={t('meterReadingHistory.editTriggerFor', { timestamp: dateTimeFormat.format(new Date(item.readingTimestamp)) })}
                    >
                      {t('meterReadingHistory.editTrigger')}
                    </Button>
                  </TableCell>
                </TableRow>
              ))}
            </TableBody>
          </Table>
        )}
      </div>

      {data && data.totalCount > 0 && (
        <div className="flex items-center justify-center gap-4">
          <Button variant="outline" disabled={loading || page <= 1} onClick={() => setPage((p) => p - 1)}>
            {t('meterReadingHistory.previousPage')}
          </Button>
          <span className="text-muted-foreground text-sm">
            {t('meterReadingHistory.pageIndicator', { page, totalPages })}
          </span>
          <Button variant="outline" disabled={loading || page >= totalPages} onClick={() => setPage((p) => p + 1)}>
            {t('meterReadingHistory.nextPage')}
          </Button>
        </div>
      )}

      {editing && (
        <EditMeterReadingDialog
          reading={editing}
          open={true}
          onOpenChange={(open) => {
            if (!open) {
              setEditing(null)
            }
          }}
          onSaved={() => {
            setEditing(null)
            // Re-fetch the current page, not reset to page 1 — the edited row's Version/
            // correction fields must reflect the save, and the household member shouldn't lose
            // their place.
            load(page)
          }}
        />
      )}
    </main>
  )
}
