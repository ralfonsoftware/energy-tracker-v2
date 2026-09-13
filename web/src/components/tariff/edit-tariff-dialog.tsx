import { useEffect, useState, type FormEvent } from 'react'
import { useTranslation } from 'react-i18next'
import { Button } from '@/components/ui/button'
import { Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle } from '@/components/ui/dialog'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import { UnitInput } from '@/components/ui/unit-input'
import { GLASS_MODAL_CLASSNAME } from '@/lib/glass-classnames'
import { ApiError, updateTariff, type TariffHistoryItemDto } from '@/lib/tariff-api'

interface EditTariffDialogProps {
  tariff: TariffHistoryItemDto
  open: boolean
  onOpenChange: (open: boolean) => void
  onSaved: () => void
}

// Mirrors edit-meter-reading-dialog.tsx's Dialog+GLASS_MODAL_CLASSNAME shell and 409-conflict
// handling exactly (ApiError.status === 409 -> conflict message, no auto-retry). AC #3's
// locked-field override is enforced server-side (EditTariff's overrideConfirmed parameter) — this
// dialog's checkbox is the required UI companion, not the actual gate: a past-dated entry's price
// fields don't get the same one-click Save a future-dated entry's do.
export function EditTariffDialog({ tariff, open, onOpenChange, onSaved }: EditTariffDialogProps) {
  const { t } = useTranslation()
  const [monthlyBaseFee, setMonthlyBaseFee] = useState(String(tariff.monthlyBaseFee))
  const [pricePerKwh, setPricePerKwh] = useState(String(tariff.pricePerKwh))
  const [currency, setCurrency] = useState(tariff.currency)
  const [contractPeriodMonths, setContractPeriodMonths] = useState(String(tariff.contractPeriodMonths))
  const [overrideConfirmed, setOverrideConfirmed] = useState(false)
  const [submitting, setSubmitting] = useState(false)
  const [error, setError] = useState<string | null>(null)

  useEffect(() => {
    if (open) {
      setMonthlyBaseFee(String(tariff.monthlyBaseFee))
      setPricePerKwh(String(tariff.pricePerKwh))
      setCurrency(tariff.currency)
      setContractPeriodMonths(String(tariff.contractPeriodMonths))
      setOverrideConfirmed(false)
      setError(null)
    }
  }, [open, tariff])

  const isLocked = new Date(tariff.contractStartDate).getTime() <= Date.now()
  const priceFieldsChanged =
    Number(monthlyBaseFee) !== tariff.monthlyBaseFee || Number(pricePerKwh) !== tariff.pricePerKwh
  const needsOverrideConfirmation = isLocked && priceFieldsChanged

  const handleSubmit = async (event: FormEvent<HTMLFormElement>) => {
    event.preventDefault()
    if (needsOverrideConfirmation && !overrideConfirmed) {
      return
    }

    setSubmitting(true)
    setError(null)

    try {
      await updateTariff(tariff.id, {
        monthlyBaseFee: Number(monthlyBaseFee),
        pricePerKwh: Number(pricePerKwh),
        currency,
        contractPeriodMonths: Number(contractPeriodMonths),
        version: tariff.version,
        overrideConfirmed,
      })
      onOpenChange(false)
      onSaved()
    } catch (err) {
      if (err instanceof ApiError && err.status === 409) {
        // Surface the conflict and let the next fetch (the list's re-fetch-after-save) supply the
        // current value — no auto-retry with a bumped version.
        setError(t('tariff.editDialog.conflictError'))
      } else {
        setError(err instanceof ApiError && err.detail ? err.detail : t('tariff.editDialog.errorGeneric'))
      }
    } finally {
      setSubmitting(false)
    }
  }

  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      <DialogContent className={GLASS_MODAL_CLASSNAME}>
        <DialogHeader>
          <DialogTitle>{t('tariff.editDialog.title')}</DialogTitle>
          <DialogDescription className="sr-only">{t('tariff.editDialog.title')}</DialogDescription>
        </DialogHeader>

        <form onSubmit={handleSubmit} className="flex flex-col gap-4">
          {isLocked && (
            <p className="text-muted-foreground text-sm">{t('tariff.editDialog.lockedNotice')}</p>
          )}

          <div className="flex flex-col gap-2">
            <Label htmlFor="edit-tariff-monthly-base-fee">{t('tariff.form.monthlyBaseFeeLabel')}</Label>
            <UnitInput
              id="edit-tariff-monthly-base-fee"
              type="number"
              inputMode="decimal"
              unit={currency}
              min="0"
              step="0.01"
              value={monthlyBaseFee}
              onChange={(event) => setMonthlyBaseFee(event.target.value)}
              disabled={submitting}
              required
            />
          </div>

          <div className="flex flex-col gap-2">
            <Label htmlFor="edit-tariff-price-per-kwh">{t('tariff.form.pricePerKwhLabel')}</Label>
            <UnitInput
              id="edit-tariff-price-per-kwh"
              type="number"
              inputMode="decimal"
              unit={`${currency}/kWh`}
              min="0.0001"
              step="0.0001"
              value={pricePerKwh}
              onChange={(event) => setPricePerKwh(event.target.value)}
              disabled={submitting}
              required
            />
          </div>

          <div className="flex flex-col gap-2">
            <Label htmlFor="edit-tariff-currency">{t('tariff.form.currencyLabel')}</Label>
            <Input
              id="edit-tariff-currency"
              value={currency}
              onChange={(event) => setCurrency(event.target.value.toUpperCase())}
              maxLength={3}
              disabled={submitting}
              required
            />
          </div>

          <div className="flex flex-col gap-2">
            <Label htmlFor="edit-tariff-contract-period">{t('tariff.form.contractPeriodLabel')}</Label>
            <UnitInput
              id="edit-tariff-contract-period"
              type="number"
              unit={t('tariff.form.months')}
              min="1"
              step="1"
              value={contractPeriodMonths}
              onChange={(event) => setContractPeriodMonths(event.target.value)}
              disabled={submitting}
              required
            />
          </div>

          {needsOverrideConfirmation && (
            // Native checkbox, not a new shadcn primitive — mirrors Story 3.10's own precedent
            // (native radios) for a one-off confirmation control this codebase has no existing
            // Checkbox component for yet.
            <label className="flex items-start gap-2 text-sm">
              <input
                type="checkbox"
                checked={overrideConfirmed}
                onChange={(event) => setOverrideConfirmed(event.target.checked)}
                disabled={submitting}
                className="mt-0.5"
              />
              {t('tariff.editDialog.overrideConfirmLabel')}
            </label>
          )}

          {error && <p className="text-destructive text-sm">{error}</p>}

          <DialogFooter>
            <Button type="button" variant="outline" onClick={() => onOpenChange(false)} disabled={submitting}>
              {t('tariff.editDialog.cancel')}
            </Button>
            <Button
              type="submit"
              variant="glass-primary"
              disabled={submitting || (needsOverrideConfirmation && !overrideConfirmed)}
            >
              {submitting ? t('tariff.editDialog.saving') : t('tariff.editDialog.save')}
            </Button>
          </DialogFooter>
        </form>
      </DialogContent>
    </Dialog>
  )
}
