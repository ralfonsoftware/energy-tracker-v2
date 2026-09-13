import { useState, type FormEvent } from 'react'
import { useTranslation } from 'react-i18next'
import { Button } from '@/components/ui/button'
import { GlassCard } from '@/components/ui/glass-card'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from '@/components/ui/select'
import { UnitInput } from '@/components/ui/unit-input'
import { ApiError, createTariff, type TariffHistoryItemDto } from '@/lib/tariff-api'

// Fixed preset list (Ralf's resolved Open Question #3) — no free-form months integer, no existing
// preset-list component in this codebase to copy so this hand-rolls a plain Select.
const CONTRACT_PERIOD_PRESETS = [1, 6, 12, 18, 24, 36] as const

interface TariffConfigurationFormProps {
  householdCurrency: string
  onCreated: (tariff: TariffHistoryItemDto) => void
}

// Create-new-entry form — no mockup exists for this story (Scope Reality Check), built against
// yearly-baseline-form.tsx/household-creation-form.tsx as the closest shipped analogs.
export function TariffConfigurationForm({ householdCurrency, onCreated }: TariffConfigurationFormProps) {
  const { t } = useTranslation()
  const [monthlyBaseFee, setMonthlyBaseFee] = useState('')
  const [pricePerKwh, setPricePerKwh] = useState('')
  // Pre-fills from Household.Currency (Ralf's resolved Open Question #4) — still independently
  // editable, Tariff.Currency remains its own column.
  const [currency, setCurrency] = useState(householdCurrency)
  const [contractStartDate, setContractStartDate] = useState('')
  const [contractPeriodMonths, setContractPeriodMonths] = useState<string>('12')
  const [submitting, setSubmitting] = useState(false)
  const [error, setError] = useState<string | null>(null)

  const isValid =
    monthlyBaseFee !== '' &&
    Number(monthlyBaseFee) >= 0 &&
    pricePerKwh !== '' &&
    Number(pricePerKwh) > 0 &&
    currency.length === 3 &&
    contractStartDate !== '' &&
    contractPeriodMonths !== ''

  const handleSubmit = async (event: FormEvent<HTMLFormElement>) => {
    event.preventDefault()
    if (!isValid) {
      return
    }

    setSubmitting(true)
    setError(null)

    try {
      const created = await createTariff({
        monthlyBaseFee: Number(monthlyBaseFee),
        pricePerKwh: Number(pricePerKwh),
        currency,
        // Date-only input ("YYYY-MM-DD") parses as UTC midnight, same conversion
        // log-reading-sheet.tsx uses for its own date/time input.
        contractStartDate: new Date(contractStartDate).toISOString(),
        contractPeriodMonths: Number(contractPeriodMonths),
      })
      setMonthlyBaseFee('')
      setPricePerKwh('')
      setContractStartDate('')
      setContractPeriodMonths('12')
      onCreated(created)
    } catch (err) {
      setError(err instanceof ApiError && err.detail ? err.detail : t('tariff.form.errorGeneric'))
    } finally {
      setSubmitting(false)
    }
  }

  return (
    <GlassCard className="flex flex-col gap-4">
      <h2 className="text-lg font-semibold">{t('tariff.form.heading')}</h2>
      <p className="text-muted-foreground text-sm">{t('tariff.form.description')}</p>

      <form className="flex flex-col gap-4" onSubmit={handleSubmit}>
        <div className="flex flex-col gap-2">
          <Label htmlFor="tariff-monthly-base-fee">{t('tariff.form.monthlyBaseFeeLabel')}</Label>
          <UnitInput
            id="tariff-monthly-base-fee"
            type="number"
            inputMode="decimal"
            unit={currency || t('tariff.form.currencyPlaceholder')}
            min="0"
            step="0.01"
            value={monthlyBaseFee}
            disabled={submitting}
            onChange={(event) => setMonthlyBaseFee(event.target.value)}
            required
          />
        </div>

        <div className="flex flex-col gap-2">
          <Label htmlFor="tariff-price-per-kwh">{t('tariff.form.pricePerKwhLabel')}</Label>
          <UnitInput
            id="tariff-price-per-kwh"
            type="number"
            inputMode="decimal"
            unit={`${currency || t('tariff.form.currencyPlaceholder')}/kWh`}
            min="0.0001"
            step="0.0001"
            value={pricePerKwh}
            disabled={submitting}
            onChange={(event) => setPricePerKwh(event.target.value)}
            required
          />
        </div>

        <div className="flex flex-col gap-2">
          <Label htmlFor="tariff-currency">{t('tariff.form.currencyLabel')}</Label>
          <Input
            id="tariff-currency"
            value={currency}
            onChange={(event) => setCurrency(event.target.value.toUpperCase())}
            placeholder={t('tariff.form.currencyPlaceholder')}
            maxLength={3}
            disabled={submitting}
            required
          />
        </div>

        <div className="flex flex-col gap-2">
          <Label htmlFor="tariff-contract-start-date">{t('tariff.form.contractStartDateLabel')}</Label>
          <Input
            id="tariff-contract-start-date"
            type="date"
            value={contractStartDate}
            onChange={(event) => setContractStartDate(event.target.value)}
            disabled={submitting}
            required
          />
        </div>

        <div className="flex flex-col gap-2">
          <Label htmlFor="tariff-contract-period">{t('tariff.form.contractPeriodLabel')}</Label>
          <Select value={contractPeriodMonths} onValueChange={setContractPeriodMonths} disabled={submitting}>
            <SelectTrigger id="tariff-contract-period">
              <SelectValue placeholder={t('tariff.form.contractPeriodLabel')} />
            </SelectTrigger>
            <SelectContent>
              {CONTRACT_PERIOD_PRESETS.map((months) => (
                <SelectItem key={months} value={String(months)}>
                  {t('tariff.form.contractPeriodMonths', { count: months })}
                </SelectItem>
              ))}
            </SelectContent>
          </Select>
        </div>

        {error && <p className="text-destructive text-sm">{error}</p>}

        <Button type="submit" variant="glass-primary" disabled={submitting || !isValid} className="self-start">
          {submitting ? t('tariff.form.saving') : t('tariff.form.submit')}
        </Button>
      </form>
    </GlassCard>
  )
}
