import { useState, type FormEvent } from 'react'
import { useTranslation } from 'react-i18next'
import { Button } from '@/components/ui/button'
import { Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle } from '@/components/ui/dialog'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import { UnitInput } from '@/components/ui/unit-input'
import { GLASS_MODAL_CLASSNAME } from '@/lib/glass-classnames'
import { toDateTimeLocalValue } from '@/lib/datetime-local'
import { resendRejectedReading } from '@/lib/meter-reading-sync'
import type { QueuedMeterReading, RejectionInfo } from '@/lib/offline-queue'

interface ResendRejectedReadingDialogProps {
  householdId: string
  // A snapshot taken when the dialog opened: sending clears the entry's `rejection`, which drops its
  // row from the card while this dialog is still open.
  reading: QueuedMeterReading
  onClose: () => void
  // The server took the corrected reading (the same refresh a foreground save triggers follows).
  onSent: () => void
  // The request got no answer: it stays pending and syncs on reconnect.
  onQueued: () => void
}

// Story 11.16 (AC #8): correct a reading the server refused and send it again under the SAME
// idempotency key (resendRejectedReading has no way to pass another one). Same Dialog shell as
// EditMeterReadingDialog; unit inside the kWh field; the conversion back to the wire format is the
// one LogReadingSheet uses, so a corrected reading is stored like a new one.
export function ResendRejectedReadingDialog({ householdId, reading, onClose, onSent, onQueued }: ResendRejectedReadingDialogProps) {
  const { t } = useTranslation()
  const [kwhValue, setKwhValue] = useState(String(reading.kwhValue))
  const [readingTimestamp, setReadingTimestamp] = useState(() => toDateTimeLocalValue(new Date(reading.readingTimestamp)))
  const [rejection, setRejection] = useState<RejectionInfo | undefined>(reading.rejection)
  const [submitting, setSubmitting] = useState(false)
  // What went wrong on the last attempt when the dialog stays open without a new server refusal.
  const [problem, setProblem] = useState<{ kind: 'unavailable'; status: number } | { kind: 'failed' } | null>(null)

  const reason = rejection ? (rejection.detail ?? t('meterReading.rejected.reasonFallback', { status: rejection.status })) : null

  const handleSubmit = async (event: FormEvent<HTMLFormElement>) => {
    event.preventDefault()
    setSubmitting(true)
    setProblem(null)
    try {
      const result = await resendRejectedReading(householdId, reading.idempotencyKey, {
        kwhValue: Number(kwhValue),
        readingTimestamp: new Date(readingTimestamp).toISOString(),
      })
      if (result.outcome === 'rejected') {
        setRejection(result.rejection)
        setSubmitting(false)
        return
      }
      if (result.outcome === 'unavailable') {
        // A transient answer (5xx, expired sign-in, rate limit) is not "offline": say so and keep the
        // dialog open. The corrected values are already stored and the entry is pending again.
        setProblem({ kind: 'unavailable', status: result.status })
        setSubmitting(false)
        return
      }
      if (result.outcome === 'sent') {
        onSent()
      } else if (result.outcome === 'queued') {
        onQueued()
      }
      onClose()
    } catch {
      // The local store could not be written (or the typed date is out of range): say so and leave the
      // dialog open so nothing is lost.
      setProblem({ kind: 'failed' })
      setSubmitting(false)
    }
  }

  return (
    <Dialog open onOpenChange={(open) => !open && !submitting && onClose()}>
      <DialogContent className={GLASS_MODAL_CLASSNAME}>
        <DialogHeader>
          <DialogTitle>{t('meterReading.rejected.dialogTitle')}</DialogTitle>
          <DialogDescription>{t('meterReading.rejected.dialogDescription')}</DialogDescription>
        </DialogHeader>

        <form onSubmit={handleSubmit} className="flex flex-col gap-4">
          {reason && (
            <div role="alert" className="bg-surface-quiet border-surface-quiet-border flex flex-col gap-0.5 rounded-xl border px-3 py-2.5">
              <span className="text-muted-foreground text-[0.65rem] font-bold tracking-wider uppercase">
                {t('meterReading.rejected.reasonLabel')}
              </span>
              <span className="text-sm break-words">{reason}</span>
            </div>
          )}

          {problem && (
            <p role="alert" className="text-sm break-words">
              {problem.kind === 'unavailable'
                ? t('meterReading.rejected.unavailable', { status: problem.status })
                : t('meterReading.rejected.failed')}
            </p>
          )}

          <div className="flex flex-col gap-2">
            <Label htmlFor="resend-reading-kwh">{t('meterReading.kwhLabel')}</Label>
            <UnitInput
              id="resend-reading-kwh"
              type="number"
              inputMode="decimal"
              unit="kWh"
              step="0.01"
              min="0.01"
              value={kwhValue}
              onChange={(event) => setKwhValue(event.target.value)}
              disabled={submitting}
              required
            />
          </div>

          <div className="flex flex-col gap-2">
            <Label htmlFor="resend-reading-timestamp">{t('meterReading.dateTimeLabel')}</Label>
            <Input
              id="resend-reading-timestamp"
              type="datetime-local"
              value={readingTimestamp}
              onChange={(event) => setReadingTimestamp(event.target.value)}
              disabled={submitting}
              required
            />
          </div>

          <DialogFooter>
            <Button type="button" variant="outline" onClick={onClose} disabled={submitting}>
              {t('meterReading.rejected.cancel')}
            </Button>
            <Button type="submit" variant="glass-primary" disabled={submitting || !kwhValue || !readingTimestamp}>
              {submitting ? t('meterReading.rejected.submitting') : t('meterReading.rejected.submit')}
            </Button>
          </DialogFooter>
        </form>
      </DialogContent>
    </Dialog>
  )
}
