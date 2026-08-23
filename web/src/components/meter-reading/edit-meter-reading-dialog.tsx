import { useEffect, useState, type FormEvent } from 'react'
import { useTranslation } from 'react-i18next'
import { Button } from '@/components/ui/button'
import { Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle } from '@/components/ui/dialog'
import { Label } from '@/components/ui/label'
import { UnitInput } from '@/components/ui/unit-input'
import { GLASS_MODAL_CLASSNAME } from '@/lib/glass-classnames'
import { ApiError, updateMeterReading, type MeterReadingHistoryItemDto } from '@/lib/meter-reading-history-api'

interface EditMeterReadingDialogProps {
  reading: MeterReadingHistoryItemDto
  open: boolean
  onOpenChange: (open: boolean) => void
  onSaved: () => void
}

// Reuses the Dialog + GLASS_MODAL_CLASSNAME shell (StatusDetailDialog/MeterRegressionPromptDialog
// precedent) rather than inventing new visual language — no mockup exists for this story (added
// post-UX-freeze). AD-16: this is a plain online-only fetch, never routed through
// meter-reading-sync.ts's offline-queue machinery — edits are explicitly not offline-queued.
export function EditMeterReadingDialog({ reading, open, onOpenChange, onSaved }: EditMeterReadingDialogProps) {
  const { t } = useTranslation()
  const [kwhValue, setKwhValue] = useState(String(reading.kwhValue))
  const [submitting, setSubmitting] = useState(false)
  const [error, setError] = useState<string | null>(null)

  useEffect(() => {
    if (open) {
      setKwhValue(String(reading.kwhValue))
      setError(null)
    }
  }, [open, reading.kwhValue])

  const handleSubmit = async (event: FormEvent<HTMLFormElement>) => {
    event.preventDefault()
    setSubmitting(true)
    setError(null)

    try {
      await updateMeterReading(reading.id, Number(kwhValue), reading.version)
      onOpenChange(false)
      onSaved()
    } catch (err) {
      if (err instanceof ApiError && err.status === 409) {
        // Surface the conflict and let the next fetch (the page's re-fetch-after-save, or the
        // household member re-opening the row) supply the current value — no auto-retry with a
        // bumped version.
        setError(t('meterReadingHistory.conflictError'))
      } else {
        setError(err instanceof ApiError && err.detail ? err.detail : t('meterReadingHistory.errorGeneric'))
      }
    } finally {
      setSubmitting(false)
    }
  }

  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      <DialogContent className={GLASS_MODAL_CLASSNAME}>
        <DialogHeader>
          <DialogTitle>{t('meterReadingHistory.editDialogTitle')}</DialogTitle>
          <DialogDescription className="sr-only">{t('meterReadingHistory.editDialogTitle')}</DialogDescription>
        </DialogHeader>

        <form onSubmit={handleSubmit} className="flex flex-col gap-4">
          <div className="flex flex-col gap-2">
            <Label htmlFor="edit-meter-reading-kwh">{t('meterReading.kwhLabel')}</Label>
            <UnitInput
              id="edit-meter-reading-kwh"
              type="number"
              inputMode="decimal"
              unit="kWh"
              step="0.01"
              min="0.01"
              value={kwhValue}
              onChange={(event) => setKwhValue(event.target.value)}
              disabled={submitting}
              required
              autoFocus
            />
          </div>

          {error && <p className="text-destructive text-sm">{error}</p>}

          <DialogFooter>
            <Button type="button" variant="outline" onClick={() => onOpenChange(false)} disabled={submitting}>
              {t('meterReadingHistory.cancel')}
            </Button>
            <Button type="submit" variant="glass-primary" disabled={submitting || !kwhValue}>
              {submitting ? t('meterReadingHistory.saving') : t('meterReadingHistory.save')}
            </Button>
          </DialogFooter>
        </form>
      </DialogContent>
    </Dialog>
  )
}
