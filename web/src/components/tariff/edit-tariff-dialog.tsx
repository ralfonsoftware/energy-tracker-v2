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

// Date-only input value ("YYYY-MM-DD") from a full ISO date-time string — same date-only
// granularity tariff-configuration-form.tsx's create form already commits to for
// ContractStartDate.
function toDateInputValue(isoDateTime: string) {
  return isoDateTime.slice(0, 10)
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
  const [contractStartDate, setContractStartDate] = useState(toDateInputValue(tariff.contractStartDate))
  const [contractPeriodMonths, setContractPeriodMonths] = useState(String(tariff.contractPeriodMonths))
  const [overrideConfirmed, setOverrideConfirmed] = useState(false)
  const [submitting, setSubmitting] = useState(false)
  const [error, setError] = useState<string | null>(null)

  useEffect(() => {
    if (open) {
      setMonthlyBaseFee(String(tariff.monthlyBaseFee))
      setPricePerKwh(String(tariff.pricePerKwh))
      setCurrency(tariff.currency)
      setContractStartDate(toDateInputValue(tariff.contractStartDate))
      setContractPeriodMonths(String(tariff.contractPeriodMonths))
      setOverrideConfirmed(false)
      setError(null)
    }
  }, [open, tariff])

  const contractStartDateChanged = contractStartDate !== toDateInputValue(tariff.contractStartDate)
  const priceFieldsChanged =
    Number(monthlyBaseFee) !== tariff.monthlyBaseFee || Number(pricePerKwh) !== tariff.pricePerKwh
  const currencyValid = /^[A-Z]{3}$/.test(currency)

  // Locked if the entry's stored ContractStartDate has already passed, OR the date currently
  // typed into the field would already be in the past — mirrors EditTariff.ExecuteAsync's own
  // wasLocked-or-willBeLocked check server-side (AC #3 is enforced there, not here; this just
  // shows the override step before the server would reject a one-click Save).
  const computeIsLocked = (now: number) => {
    const wasLocked = new Date(tariff.contractStartDate).getTime() <= now
    const willBeLocked = contractStartDateChanged ? new Date(contractStartDate).getTime() <= now : wasLocked
    return wasLocked || willBeLocked
  }

  const isLocked = computeIsLocked(Date.now())
  const needsOverrideConfirmation = isLocked && (priceFieldsChanged || contractStartDateChanged)

  const handleSubmit = async (event: FormEvent<HTMLFormElement>) => {
    event.preventDefault()
    if (!currencyValid) {
      return
    }

    // Re-checked against the current clock, not the value computed at the last render — closes
    // the gap where the dialog sits open across the exact moment this entry's ContractStartDate
    // passes. A failed check here also updates `error`, which forces the re-render that makes the
    // override checkbox actually appear.
    if (computeIsLocked(Date.now()) && (priceFieldsChanged || contractStartDateChanged) && !overrideConfirmed) {
      setError(t('tariff.editDialog.lockedNotice'))
      return
    }

    setSubmitting(true)
    setError(null)

    try {
      await updateTariff(tariff.id, {
        monthlyBaseFee: Number(monthlyBaseFee),
        pricePerKwh: Number(pricePerKwh),
        currency,
        ...(contractStartDateChanged ? { contractStartDate: new Date(contractStartDate).toISOString() } : {}),
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
            <Label htmlFor="edit-tariff-contract-start-date">{t('tariff.form.contractStartDateLabel')}</Label>
            <Input
              id="edit-tariff-contract-start-date"
              type="date"
              value={contractStartDate}
              onChange={(event) => setContractStartDate(event.target.value)}
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
              disabled={submitting || (needsOverrideConfirmation && !overrideConfirmed) || !currencyValid}
            >
              {submitting ? t('tariff.editDialog.saving') : t('tariff.editDialog.save')}
            </Button>
          </DialogFooter>
        </form>
      </DialogContent>
    </Dialog>
  )
}
