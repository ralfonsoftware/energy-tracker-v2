import { useEffect, useRef, useState } from 'react'
import { TriangleAlert } from 'lucide-react'
import { useTranslation } from 'react-i18next'
import { Button } from '@/components/ui/button'
import { QuietCard } from '@/components/ui/quiet-card'
import { useRejectedReadings } from '@/hooks/use-rejected-readings'
import { remove, type QueuedMeterReading } from '@/lib/offline-queue'
import { ResendRejectedReadingDialog } from './resend-rejected-reading-dialog'

interface RejectedReadingsCardProps {
  householdId: string
  locale: string
  // A corrected reading the server accepted: the caller refreshes what a saved reading changes
  // (the open regression prompt, Status), exactly as after a foreground save.
  onResent: () => void
}

const NOTICE_MS = 8000

// Where keyboard focus goes after the inline discard step swaps or removes the focused button
// (WCAG 2.4.3): without it focus falls back to the page.
type FocusTarget = { kind: 'keep' } | { kind: 'heading' } | { kind: 'discard'; key: string }

// Story 11.16 (audit C4, AC #2, #6-#9): readings a member typed offline that the server refused wait
// here, on the Dashboard under the Status card, until they are corrected and resent or discarded.
// Quiet tier (mockups/key-offline-attention-2026-10-28.html, option A confirmed by Ralf): it must not
// compete with the Status card. Renders nothing when nothing needs attention, so the empty Dashboard
// is exactly as before. The dialog is owned here, not by a row: sending makes the entry pending
// again, which drops its row while the dialog is still open.
export function RejectedReadingsCard({ householdId, locale, onResent }: RejectedReadingsCardProps) {
  const { t } = useTranslation()
  const readings = useRejectedReadings(householdId)
  const [resending, setResending] = useState<QueuedMeterReading | null>(null)
  const [confirmingKey, setConfirmingKey] = useState<string | null>(null)
  const [notice, setNotice] = useState<string | null>(null)
  const pendingFocus = useRef<FocusTarget | null>(null)
  const keepRef = useRef<HTMLButtonElement>(null)
  const headingRef = useRef<HTMLHeadingElement>(null)
  const listRef = useRef<HTMLUListElement>(null)

  // Runs after every render; a request set by a click is consumed once the new controls are mounted.
  useEffect(() => {
    const target = pendingFocus.current
    if (target === null) {
      return
    }
    pendingFocus.current = null
    if (target.kind === 'keep') {
      keepRef.current?.focus()
    } else if (target.kind === 'heading') {
      headingRef.current?.focus()
    } else {
      Array.from(listRef.current?.querySelectorAll<HTMLElement>('[data-discard-key]') ?? [])
        .find((element) => element.dataset.discardKey === target.key)
        ?.focus()
    }
  })

  useEffect(() => {
    if (notice === null) {
      return
    }
    const timer = setTimeout(() => setNotice(null), NOTICE_MS)
    return () => clearTimeout(timer)
  }, [notice])

  const numberFormat = new Intl.NumberFormat(locale, { maximumFractionDigits: 2 })
  const dateTimeFormat = new Intl.DateTimeFormat(locale, { dateStyle: 'medium', timeStyle: 'short' })

  const dialog = resending && (
    <ResendRejectedReadingDialog
      householdId={householdId}
      reading={resending}
      onClose={() => setResending(null)}
      onSent={onResent}
      onQueued={() => setNotice(t('meterReading.savedOffline'))}
    />
  )

  if (readings.length === 0) {
    // The entry just resent went back to pending, so its row is gone; the message still has to land.
    return (
      <>
        {notice && (
          <p role="status" className="text-muted-foreground text-sm">
            {notice}
          </p>
        )}
        {dialog}
      </>
    )
  }

  return (
    <>
      <QuietCard aria-labelledby="rejected-readings-title" className="gap-2 p-4">
        <header className="flex items-start gap-2.5">
          <span className="bg-muted text-muted-foreground flex size-7 shrink-0 items-center justify-center rounded-[10px]">
            <TriangleAlert className="size-4" aria-hidden="true" />
          </span>
          <div className="flex flex-col gap-0.5">
            <h2 id="rejected-readings-title" ref={headingRef} tabIndex={-1} className="text-sm font-bold outline-none">
              {t('meterReading.rejected.title', { count: readings.length })}
            </h2>
            <p className="text-muted-foreground text-xs">{t('meterReading.rejected.description', { count: readings.length })}</p>
          </div>
        </header>

        {notice && (
          <p role="status" className="text-muted-foreground text-sm">
            {notice}
          </p>
        )}

        <ul ref={listRef} aria-live="polite" className="flex flex-col">
          {readings.map((reading) => {
            const kwh = numberFormat.format(reading.kwhValue)
            const timestamp = dateTimeFormat.format(new Date(reading.readingTimestamp))
            const rejection = reading.rejection
            const reason = rejection ? (rejection.detail ?? t('meterReading.rejected.reasonFallback', { status: rejection.status })) : ''
            const confirming = confirmingKey === reading.idempotencyKey
            return (
              <li
                key={reading.idempotencyKey}
                className="border-surface-quiet-border flex flex-col gap-2.5 border-t py-3 first:mt-1 wide:flex-row wide:items-center wide:justify-between wide:gap-5"
              >
                <div className="flex min-w-0 flex-col gap-1">
                  <div className="flex flex-wrap items-baseline gap-x-2.5">
                    <span className="text-base font-bold tabular-nums">
                      {kwh} <span className="text-muted-foreground text-xs font-semibold">kWh</span>
                    </span>
                    <span className="text-muted-foreground text-xs">{timestamp}</span>
                  </div>
                  <p className="text-sm break-words">{reason}</p>
                </div>

                {confirming ? (
                  <div
                    role="group"
                    aria-label={t('meterReading.rejected.discardFor', { kwh, timestamp })}
                    className="flex flex-col gap-2 wide:flex-row wide:items-center wide:gap-3.5"
                  >
                    <span className="text-sm font-semibold">{t('meterReading.rejected.discardConfirm')}</span>
                    <span className="flex gap-2">
                      <Button
                        variant="outline"
                        size="sm"
                        className="border-foreground/60 rounded-full"
                        onClick={async () => {
                          try {
                            await remove(reading.idempotencyKey)
                            pendingFocus.current = { kind: 'heading' }
                          } catch {
                            // The device store could not be written: the row stays, back to its two actions.
                            pendingFocus.current = { kind: 'discard', key: reading.idempotencyKey }
                          } finally {
                            setConfirmingKey(null)
                          }
                        }}
                      >
                        {t('meterReading.rejected.discardConfirmAction')}
                      </Button>
                      <Button
                        ref={keepRef}
                        variant="ghost"
                        size="sm"
                        className="rounded-full"
                        onClick={() => {
                          pendingFocus.current = { kind: 'discard', key: reading.idempotencyKey }
                          setConfirmingKey(null)
                        }}
                      >
                        {t('meterReading.rejected.discardKeep')}
                      </Button>
                    </span>
                  </div>
                ) : (
                  <span className="flex flex-wrap gap-2">
                    <Button
                      variant="ghost"
                      size="sm"
                      className="bg-nav-chrome-active-bg text-nav-chrome-active-foreground rounded-full"
                      aria-label={t('meterReading.rejected.resendFor', { kwh, timestamp })}
                      onClick={() => setResending(reading)}
                    >
                      {t('meterReading.rejected.resend')}
                    </Button>
                    <Button
                      variant="outline"
                      size="sm"
                      className="rounded-full"
                      aria-label={t('meterReading.rejected.discardFor', { kwh, timestamp })}
                      data-discard-key={reading.idempotencyKey}
                      onClick={() => {
                        pendingFocus.current = { kind: 'keep' }
                        setConfirmingKey(reading.idempotencyKey)
                      }}
                    >
                      {t('meterReading.rejected.discard')}
                    </Button>
                  </span>
                )}
              </li>
            )
          })}
        </ul>
      </QuietCard>
      {dialog}
    </>
  )
}
