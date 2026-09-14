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
  // Story 5.2's frontend gating recommendation: lets TariffRadarPage know whether any Tariff
  // entry exists yet, without a second duplicate "get current tariff" HTTP round trip — reuses
  // this list's own already-fetched page data instead.
  onLoaded?: (page: TariffHistoryPageDto) => void
}

const PAGE_SIZE = 20

// Card-list shape mirrors MeterReadingsCard's table+pagination composition (this repo's
// established list-of-history-entries pattern) — no mockup exists for this story's Configuration/
// history surface (Scope Reality Check).
export function TariffHistoryList({ locale, refreshNonce, onLoaded }: TariffHistoryListProps) {
  const { t } = useTranslation()
  const [page, setPage] = useState(1)
  const [data, setData] = useState<TariffHistoryPageDto | null>(null)
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState(false)
  const [editing, setEditing] = useState<TariffHistoryItemDto | null>(null)

  const load = useCallback(
    (targetPage: number) => {
      let cancelled = false
      setLoading(true)
      setError(false)
      fetchTariffHistory(targetPage, PAGE_SIZE)
        .then((result) => {
          if (cancelled) {
            return
          }
          setData(result)
          onLoaded?.(result)
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
    },
    [onLoaded],
  )

  useEffect(() => load(page), [load, page, refreshNonce])

  // Fixed-decimal, matching each field's own EF Core column precision (AC #5) — never floating-
  // point, so trailing zeros (e.g. base fee's €12.50) are never dropped.
  const baseFeeFormat = new Intl.NumberFormat(locale, { minimumFractionDigits: 2, maximumFractionDigits: 2 })
  const priceFormat = new Intl.NumberFormat(locale, { minimumFractionDigits: 4, maximumFractionDigits: 4 })
  const dateFormat = new Intl.DateTimeFormat(locale, { dateStyle: 'medium' })

  const totalPages = data ? Math.max(1, Math.ceil(data.totalCount / data.pageSize)) : 1

  // TariffFieldCorrectionResponse's own doc comment promises the frontend parses/formats each
  // correction's locale-neutral, invariant-culture oldValue per field type and the household's
  // Locale (AD-18) — this is that parse/format step.
  const formatCorrectionValue = (fieldName: string, rawValue: string) => {
    switch (fieldName) {
      case 'MonthlyBaseFee':
        return baseFeeFormat.format(Number(rawValue))
      case 'PricePerKwh':
        return priceFormat.format(Number(rawValue))
      case 'ContractStartDate':
        return dateFormat.format(new Date(rawValue))
      default:
        return rawValue
    }
  }

  const formatCorrection = (item: TariffHistoryItemDto) =>
    item.corrections.map((correction) => (
      <span key={correction.fieldName} className="text-muted-foreground text-xs">
        {t('tariff.history.correctedField', {
          field: t(`tariff.history.fieldName.${correction.fieldName}`),
          oldValue: formatCorrectionValue(correction.fieldName, correction.oldValue),
        })}
      </span>
    ))

  return (
    <GlassCard className="flex flex-col gap-3">
      <h2 className="text-lg font-semibold">{t('tariff.history.heading')}</h2>

      <div aria-live="polite">
        {loading && <p className="text-muted-foreground text-sm">{t('tariff.history.loading')}</p>}

        {!loading && error && (
          <div className="flex items-center gap-2">
            <p className="text-destructive text-sm">{t('tariff.history.loadError')}</p>
            <Button variant="outline" size="sm" onClick={() => load(page)}>
              {t('tariff.history.retry')}
            </Button>
          </div>
        )}

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
                    {baseFeeFormat.format(item.monthlyBaseFee)} {item.currency}
                  </TableCell>
                  <TableCell className="tabular-nums">
                    {priceFormat.format(item.pricePerKwh)} {item.currency}/kWh
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
