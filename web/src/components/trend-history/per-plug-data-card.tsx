import { useCallback, useEffect, useState } from 'react'
import { useTranslation } from 'react-i18next'
import { GlassCard } from '@/components/ui/glass-card'
import { fetchPerPlugMeasuredData, type RoomMeasuredDataDto } from '@/lib/smart-plug-reading-api'

interface PerPlugDataCardProps {
  locale: string
}

// Structure per mockups/density-trend-history.html (Moderate density, UX-DR6): a static <h3>
// heading (not a toggle) + one collapsed <details> per Room, a nested collapsed <details> per
// Power Point inside it, and flat (non-<details>) rows per Device inside that — Devices are
// leaves, never further nested. This is a structurally different disclosure shape than
// MeterReadingsCard's single outer details/summary toggle around the whole card, so no extra
// outer collapse wrapper goes around the tree itself (Story 4.2).
export function PerPlugDataCard({ locale }: PerPlugDataCardProps) {
  const { t } = useTranslation()
  const [rooms, setRooms] = useState<RoomMeasuredDataDto[]>([])
  const [loading, setLoading] = useState(true)
  // Distinguishes "genuinely no Smart Plug data yet" from "the fetch failed" — same discipline
  // TrendHistoryPage's chartLoadError already applies to the chart's own fetch.
  const [error, setError] = useState(false)

  const load = useCallback(() => {
    let cancelled = false
    setLoading(true)
    setError(false)
    fetchPerPlugMeasuredData()
      .then((result) => {
        if (!cancelled) {
          setRooms(result)
        }
      })
      .catch(() => {
        if (!cancelled) {
          setError(true)
        }
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

  useEffect(() => load(), [load])

  const numberFormat = new Intl.NumberFormat(locale, { maximumFractionDigits: 2 })

  return (
    <GlassCard>
      <h3 className="text-sm font-semibold">{t('trendHistory.perPlugCard.heading')}</h3>

      <div className="mt-3" aria-live="polite">
        {loading && <p className="text-muted-foreground text-sm">{t('trendHistory.perPlugCard.loading')}</p>}

        {!loading && error && <p className="text-destructive text-sm">{t('trendHistory.perPlugCard.loadError')}</p>}

        {!loading && !error && rooms.length === 0 && (
          <p className="text-muted-foreground text-sm">{t('trendHistory.perPlugCard.emptyState')}</p>
        )}

        {!loading && !error && rooms.length > 0 && (
          <ul>
            {rooms.map((room) => (
              <li key={room.roomName}>
                <details className="group border-b border-border/50 py-1 last:border-b-0">
                  <summary className="flex cursor-pointer list-none items-center justify-between text-sm font-semibold [&::-webkit-details-marker]:hidden">
                    <span>{room.roomName}</span>
                    <span className="tabular-nums">{numberFormat.format(room.totalKwh)} kWh</span>
                  </summary>

                  <ul className="mt-1 pl-3">
                    {room.powerPoints.map((powerPoint) => (
                      <li key={powerPoint.powerPointName}>
                        <details className="group/pp py-1">
                          <summary className="flex cursor-pointer list-none items-center justify-between text-sm [&::-webkit-details-marker]:hidden">
                            <span>{powerPoint.powerPointName}</span>
                            <span className="tabular-nums">{numberFormat.format(powerPoint.totalKwh)} kWh</span>
                          </summary>

                          <ul className="mt-1 pl-3">
                            {powerPoint.devices.map((device) => (
                              <li
                                key={device.deviceName}
                                className="text-muted-foreground flex items-center justify-between text-xs"
                              >
                                <span>{device.deviceName}</span>
                                <span className="tabular-nums">{numberFormat.format(device.totalKwh)} kWh</span>
                              </li>
                            ))}
                          </ul>
                        </details>
                      </li>
                    ))}
                  </ul>
                </details>
              </li>
            ))}
          </ul>
        )}
      </div>

      <p className="text-muted-foreground mt-3 border-t border-border/50 pt-2 text-xs">
        {t('trendHistory.perPlugCard.caveat')}
      </p>
    </GlassCard>
  )
}
