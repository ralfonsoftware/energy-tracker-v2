import { useCallback, useEffect, useState } from 'react'
import { useTranslation } from 'react-i18next'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { GlassCard } from '@/components/ui/glass-card'
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from '@/components/ui/table'
import { fetchTariffHistory, type TariffHistoryItemDto, type TariffHistoryPageDto } from '@/lib/tariff-api'
import { EditTariffDialog } from './edit-tariff-dialog'

interface TariffHistoryListProps {
  locale: string
  refreshNonce: number
}

const PAGE_SIZE = 20

// Card-list shape mirrors MeterReadingsCard's table+pagination composition (this repo's
// established list-of-history-entries pattern) — no mockup exists for this story's Configuration/
// history surface (Scope Reality Check).
export function TariffHistoryList({ locale, refreshNonce }: TariffHistoryListProps) {
  const { t } = useTranslation()
  const [page, setPage] = useState(1)
  const [data, setData] = useState<TariffHistoryPageDto | null>(null)
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState(false)
  const [editing, setEditing] = useState<TariffHistoryItemDto | null>(null)

  const load = useCallback((targetPage: number) => {
    let cancelled = false
    setLoading(true)
    setError(false)
    fetchTariffHistory(targetPage, PAGE_SIZE)
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

  useEffect(() => load(page), [load, page, refreshNonce])

  const numberFormat = new Intl.NumberFormat(locale, { maximumFractionDigits: 4 })
  const dateFormat = new Intl.DateTimeFormat(locale, { dateStyle: 'medium' })

  const totalPages = data ? Math.max(1, Math.ceil(data.totalCount / data.pageSize)) : 1

  const formatCorrection = (item: TariffHistoryItemDto) =>
    item.corrections.map((correction) => (
      <span key={correction.fieldName} className="text-muted-foreground text-xs">
        {t('tariff.history.correctedField', {
          field: t(`tariff.history.fieldName.${correction.fieldName}`),
          oldValue: correction.oldValue,
        })}
      </span>
    ))

  return (
    <GlassCard className="flex flex-col gap-3">
      <h2 className="text-lg font-semibold">{t('tariff.history.heading')}</h2>

      <div aria-live="polite">
        {loading && <p className="text-muted-foreground text-sm">{t('tariff.history.loading')}</p>}

        {!loading && error && <p className="text-destructive text-sm">{t('tariff.history.loadError')}</p>}

        {!loading && !error && data && data.totalCount === 0 && (
          <p className="text-muted-foreground text-sm">{t('tariff.history.emptyState')}</p>
        )}

        {!loading && !error && data && data.totalCount > 0 && (
          <Table>
            <TableHeader>
              <TableRow>
                <TableHead>{t('tariff.history.periodColumn')}</TableHead>
                <TableHead>{t('tariff.history.baseFeeColumn')}</TableHead>
                <TableHead>{t('tariff.history.priceColumn')}</TableHead>
                <TableHead />
              </TableRow>
            </TableHeader>
            <TableBody>
              {data.items.map((item) => (
                <TableRow key={item.id}>
                  <TableCell>
                    <div className="flex flex-col gap-1">
                      <div className="flex items-center gap-2">
                        <span className="font-semibold">
                          {t('tariff.history.periodRange', {
                            start: dateFormat.format(new Date(item.contractStartDate)),
                            end: item.effectiveUntil ? dateFormat.format(new Date(item.effectiveUntil)) : t('tariff.history.ongoing'),
                          })}
                        </span>
                        {item.isCurrent && <Badge variant="outline">{t('tariff.history.currentBadge')}</Badge>}
                      </div>
                      {formatCorrection(item)}
                    </div>
                  </TableCell>
                  <TableCell className="tabular-nums">
                    {numberFormat.format(item.monthlyBaseFee)} {item.currency}
                  </TableCell>
                  <TableCell className="tabular-nums">
                    {numberFormat.format(item.pricePerKwh)} {item.currency}/kWh
                  </TableCell>
                  <TableCell>
                    <Button
                      variant="outline"
                      size="sm"
                      onClick={() => setEditing(item)}
                      aria-label={t('tariff.history.editTriggerFor', {
                        start: dateFormat.format(new Date(item.contractStartDate)),
                      })}
                    >
                      {t('tariff.history.editTrigger')}
                    </Button>
                  </TableCell>
                </TableRow>
              ))}
            </TableBody>
          </Table>
        )}
      </div>

      {data && data.totalCount > 0 && (
        <div className="flex items-center justify-center gap-4 pb-1">
          <Button variant="outline" disabled={loading || page <= 1} onClick={() => setPage((p) => p - 1)}>
            {t('tariff.history.previousPage')}
          </Button>
          <span className="text-muted-foreground text-sm">{t('tariff.history.pageIndicator', { page, totalPages })}</span>
          <Button variant="outline" disabled={loading || page >= totalPages} onClick={() => setPage((p) => p + 1)}>
            {t('tariff.history.nextPage')}
          </Button>
        </div>
      )}

      {editing && (
        <EditTariffDialog
          tariff={editing}
          open={true}
          onOpenChange={(open) => {
            if (!open) {
              setEditing(null)
            }
          }}
          onSaved={() => {
            setEditing(null)
            load(page)
          }}
        />
      )}
    </GlassCard>
  )
}
