import { useEffect, useState, type FormEvent } from 'react'
import { useTranslation } from 'react-i18next'
import { Button } from '@/components/ui/button'
import { Dialog, DialogContent, DialogDescription, DialogHeader, DialogTitle } from '@/components/ui/dialog'
import { Label } from '@/components/ui/label'
import { UnitInput } from '@/components/ui/unit-input'
import { GLASS_MODAL_CLASSNAME } from '@/lib/glass-classnames'
import { cn } from '@/lib/utils'
import { ApiError, resolveMeterRegressionPrompt, type MeterRegressionPromptDto } from '@/lib/meter-regression-api'

// This is a normal classification step the household resolves in one tap, not a system error
// (AC #8, UX-DR4) — never dismissible without resolving (AC #5: no silent expiry, no default
// classification), so the built-in Dialog close (X) button is hidden and Escape/outside-click are
// suppressed. No onOpenChange is wired either — Radix's Close/Escape/outside-click handlers are
// no-ops without one, which is what keeps the controlled `open` prop (driven by `prompt !== null`)
// from ever changing on its own.
const HIDE_CLOSE_BUTTON_CLASSNAME = '[&>[data-slot=dialog-close-button]]:hidden'

interface MeterRegressionPromptDialogProps {
  prompt: MeterRegressionPromptDto | null
  onResolved: () => void
}

export function MeterRegressionPromptDialog({ prompt, onResolved }: MeterRegressionPromptDialogProps) {
  const { t } = useTranslation()
  const [mode, setMode] = useState<'choice' | 'rollover'>('choice')
  const [digitCapacityKwh, setDigitCapacityKwh] = useState('')
  const [submitting, setSubmitting] = useState(false)
  const [error, setError] = useState<string | null>(null)

  // A newly-arrived prompt (including the next one queued behind a just-resolved prompt, AC #6)
  // always starts back at the choice step, pre-filling the digit capacity from this new prompt's
  // own MainMeterDigitCapacityKwh, not whatever was left over from a previous prompt's session.
  useEffect(() => {
    setMode('choice')
    setDigitCapacityKwh(prompt?.mainMeterDigitCapacityKwh != null ? String(prompt.mainMeterDigitCapacityKwh) : '')
    setError(null)
  }, [prompt?.id, prompt?.mainMeterDigitCapacityKwh])

  const handleReset = async () => {
    if (!prompt) {
      return
    }

    setSubmitting(true)
    setError(null)
    try {
      await resolveMeterRegressionPrompt(prompt.id, 'reset')
      onResolved()
    } catch (err) {
      setError(err instanceof ApiError && err.detail ? err.detail : t('meterRegression.errorGeneric'))
    } finally {
      setSubmitting(false)
    }
  }

  const handleRolloverConfirm = async (event: FormEvent<HTMLFormElement>) => {
    event.preventDefault()
    if (!prompt) {
      return
    }

    setSubmitting(true)
    setError(null)
    try {
      await resolveMeterRegressionPrompt(prompt.id, 'rollover', Number(digitCapacityKwh))
      onResolved()
    } catch (err) {
      setError(err instanceof ApiError && err.detail ? err.detail : t('meterRegression.errorGeneric'))
    } finally {
      setSubmitting(false)
    }
  }

  return (
    <Dialog open={prompt !== null}>
      <DialogContent
        className={cn(GLASS_MODAL_CLASSNAME, HIDE_CLOSE_BUTTON_CLASSNAME)}
        onEscapeKeyDown={(event) => event.preventDefault()}
        onPointerDownOutside={(event) => event.preventDefault()}
      >
        {prompt && (
          <>
            <DialogHeader>
              <DialogTitle>{t('meterRegression.title')}</DialogTitle>
              <DialogDescription>
                {t('meterRegression.description', {
                  kwh: prompt.readingKwhValue,
                  previousKwh: prompt.previousReadingKwhValue,
                })}
              </DialogDescription>
            </DialogHeader>

            {error && <p className="text-destructive text-sm">{error}</p>}

            {mode === 'choice' ? (
              <div className="flex flex-col gap-2.5">
                <Button
                  type="button"
                  variant="outline"
                  className="h-auto flex-col items-start gap-1 py-3 text-left whitespace-normal"
                  onClick={handleReset}
                  disabled={submitting}
                >
                  <span className="font-semibold">{t('meterRegression.resetAction')}</span>
                  <span className="text-muted-foreground text-xs font-normal">
                    {t('meterRegression.resetActionDescription')}
                  </span>
                </Button>
                <Button
                  type="button"
                  variant="outline"
                  className="h-auto flex-col items-start gap-1 py-3 text-left whitespace-normal"
                  onClick={() => setMode('rollover')}
                  disabled={submitting}
                >
                  <span className="font-semibold">{t('meterRegression.rolloverAction')}</span>
                  <span className="text-muted-foreground text-xs font-normal">
                    {t('meterRegression.rolloverActionDescription')}
                  </span>
                </Button>
              </div>
            ) : (
              <form onSubmit={handleRolloverConfirm} className="flex flex-col gap-4">
                <div className="flex flex-col gap-2">
                  <Label htmlFor="meter-regression-digit-capacity">{t('meterRegression.digitCapacityLabel')}</Label>
                  <UnitInput
                    id="meter-regression-digit-capacity"
                    type="number"
                    inputMode="decimal"
                    unit="kWh"
                    step="0.01"
                    min="0.01"
                    value={digitCapacityKwh}
                    onChange={(event) => setDigitCapacityKwh(event.target.value)}
                    disabled={submitting}
                    required
                    autoFocus
                  />
                </div>

                <Button type="submit" variant="glass-primary" disabled={submitting || !digitCapacityKwh}>
                  {submitting ? t('meterRegression.confirming') : t('meterRegression.confirm')}
                </Button>
              </form>
            )}
          </>
        )}
      </DialogContent>
    </Dialog>
  )
}
