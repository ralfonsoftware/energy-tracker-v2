import { useState, type FormEvent, type ReactNode } from 'react'
import { useTranslation } from 'react-i18next'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import {
  Sheet,
  SheetContent,
  SheetDescription,
  SheetHeader,
  SheetTitle,
  SheetTrigger,
} from '@/components/ui/sheet'
import { attemptSend, ApiError } from '@/lib/meter-reading-sync'

// datetime-local's value format is local time with no offset (YYYY-MM-DDTHH:mm) — this is what
// both the <input> itself and `new Date(value)` (which interprets it as local time) expect.
function toDateTimeLocalValue(date: Date): string {
  const pad = (n: number) => String(n).padStart(2, '0')
  return `${date.getFullYear()}-${pad(date.getMonth() + 1)}-${pad(date.getDate())}T${pad(date.getHours())}:${pad(date.getMinutes())}`
}

// UX-DR8's pill/gradient primary-action-button system, reused here — this story's only budget
// for that styling (Dev Notes: the polished Dashboard trigger itself is Story 2.5's deliverable).
// {components.primary-action-button.*} has no rendered mock/literal token values yet (Dev Notes),
// so this is a reasonable interpretation off the wired --primary token, not a documented spec.
const SAVE_BUTTON_CLASSNAME =
  'h-11 min-w-11 rounded-full border-0 bg-gradient-to-b from-primary to-primary/85 px-6 text-primary-foreground shadow-lg shadow-primary/30 ' +
  'transition-[transform,box-shadow] duration-150 ease-out hover:from-primary hover:to-primary/85 ' +
  'active:scale-[0.965] active:shadow-sm motion-reduce:transition-none'

export function LogReadingSheet({ trigger }: { trigger: ReactNode }) {
  const { t, i18n } = useTranslation()
  const [open, setOpen] = useState(false)
  const [kwhValue, setKwhValue] = useState('')
  const [readingTimestamp, setReadingTimestamp] = useState(() => toDateTimeLocalValue(new Date()))
  const [submitting, setSubmitting] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [confirmation, setConfirmation] = useState<string | null>(null)

  const handleOpenChange = (next: boolean) => {
    if (submitting) {
      return
    }
    if (next) {
      setKwhValue('')
      setReadingTimestamp(toDateTimeLocalValue(new Date()))
      setError(null)
      setConfirmation(null)
    }
    setOpen(next)
  }

  const handleSubmit = async (event: FormEvent<HTMLFormElement>) => {
    event.preventDefault()

    // Generated at the moment of the tap, before any network attempt (AD-16) — generating it
    // earlier (e.g. on sheet-open) would let an abandoned-then-reopened sheet reuse a stale key.
    const idempotencyKey = crypto.randomUUID()
    setSubmitting(true)
    setError(null)

    try {
      const result = await attemptSend({
        kwhValue: Number(kwhValue),
        readingTimestamp: new Date(readingTimestamp).toISOString(),
        idempotencyKey,
      })

      if (result.outcome === 'sent') {
        const time = new Date(result.reading.readingTimestamp).toLocaleTimeString(i18n.language, {
          hour: '2-digit',
          minute: '2-digit',
        })
        setConfirmation(t('meterReading.savedConfirmation', { kwh: result.reading.kwhValue, time }))
      } else {
        setConfirmation(t('meterReading.savedOffline'))
      }

      setOpen(false)
    } catch (err) {
      setError(err instanceof ApiError && err.detail ? err.detail : t('meterReading.errorGeneric'))
    } finally {
      setSubmitting(false)
    }
  }

  return (
    <div className="flex flex-col items-center gap-2">
      <Sheet open={open} onOpenChange={handleOpenChange}>
        <SheetTrigger asChild>{trigger}</SheetTrigger>
        <SheetContent side="bottom" className="rounded-t-[28px] rounded-b-none">
          <form onSubmit={handleSubmit} className="flex flex-col gap-4 px-4 pb-4">
            <SheetHeader className="px-0">
              <SheetTitle>{t('meterReading.sheetTitle')}</SheetTitle>
              <SheetDescription>{t('meterReading.sheetDescription')}</SheetDescription>
            </SheetHeader>

            <div className="flex flex-col gap-2">
              <Label htmlFor="meter-reading-kwh">{t('meterReading.kwhLabel')}</Label>
              <Input
                id="meter-reading-kwh"
                type="number"
                inputMode="decimal"
                step="0.01"
                min="0.01"
                className="tabular-nums"
                value={kwhValue}
                onChange={(event) => setKwhValue(event.target.value)}
                disabled={submitting}
                required
                autoFocus
              />
            </div>

            <div className="flex flex-col gap-2">
              <Label htmlFor="meter-reading-timestamp">{t('meterReading.dateTimeLabel')}</Label>
              <Input
                id="meter-reading-timestamp"
                type="datetime-local"
                value={readingTimestamp}
                onChange={(event) => setReadingTimestamp(event.target.value)}
                disabled={submitting}
                required
              />
            </div>

            {error && <p className="text-destructive text-sm">{error}</p>}

            <Button type="submit" className={SAVE_BUTTON_CLASSNAME} disabled={submitting || !kwhValue}>
              {submitting ? t('meterReading.saving') : t('meterReading.save')}
            </Button>
          </form>
        </SheetContent>
      </Sheet>

      {confirmation && (
        <p role="status" className="text-muted-foreground text-sm">
          {confirmation}
        </p>
      )}
    </div>
  )
}
