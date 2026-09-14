import { useState, type FormEvent } from 'react'
import { useTranslation } from 'react-i18next'
import { Button } from '@/components/ui/button'
import { GlassCard } from '@/components/ui/glass-card'
import { Label } from '@/components/ui/label'
import { UnitInput } from '@/components/ui/unit-input'
import { ApiError, compareTariff, type TariffComparisonDto } from '@/lib/tariff-api'

interface TariffComparisonFormProps {
  // The current Tariff's own currency (not necessarily Household.Currency, which is a separate,
  // independently-editable field — Tariff.cs's own doc comment) — this is the currency the
  // comparison result is actually computed and echoed in, so candidate fields must be labeled
  // with it, not the household default.
  currency: string
  locale: string
}

// A candidate Tariff comparison — scratch/exploratory (FR-11), never persisted. No mockup exists
// for this exact form shape (Scope Reality Check); field patterns mirror
// tariff-configuration-form.tsx's UnitInput/validation-before-submit/ApiError conventions.
export function TariffComparisonForm({ currency, locale }: TariffComparisonFormProps) {
  const { t } = useTranslation()
  const [candidateMonthlyBaseFee, setCandidateMonthlyBaseFee] = useState('')
  const [candidatePricePerKwh, setCandidatePricePerKwh] = useState('')
  const [candidateSwitchingBonus, setCandidateSwitchingBonus] = useState('')
  const [submitting, setSubmitting] = useState(false)
  const [error, setError] = useState<string | null>(null)
  // undefined = no comparison requested yet; null = requested but undefined (AC #3, no pace/no
  // current Tariff); a value = a computed result.
  const [result, setResult] = useState<TariffComparisonDto | null | undefined>(undefined)

  const isValid =
    candidateMonthlyBaseFee !== '' &&
    Number(candidateMonthlyBaseFee) >= 0 &&
    candidatePricePerKwh !== '' &&
    Number(candidatePricePerKwh) > 0 &&
    (candidateSwitchingBonus === '' || Number(candidateSwitchingBonus) >= 0)

  const handleSubmit = async (event: FormEvent<HTMLFormElement>) => {
    event.preventDefault()
    if (!isValid) {
      return
    }

    setSubmitting(true)
    setError(null)

    try {
      const comparison = await compareTariff({
        candidateMonthlyBaseFee: Number(candidateMonthlyBaseFee),
        candidatePricePerKwh: Number(candidatePricePerKwh),
        // Optional field (FR-11) — defaults to 0 when left blank, not required.
        candidateSwitchingBonus: candidateSwitchingBonus === '' ? 0 : Number(candidateSwitchingBonus),
      })
      setResult(comparison)
    } catch (err) {
      // Clear a prior successful comparison's stale result — otherwise it stays rendered
      // underneath the new error message, implying it still applies to the rejected candidate.
      setResult(undefined)
      setError(err instanceof ApiError && err.detail ? err.detail : t('tariff.compare.errorGeneric'))
    } finally {
      setSubmitting(false)
    }
  }

  // Fixed-decimal money display (NFR6) — minimumFractionDigits explicitly set alongside
  // maximumFractionDigits, guarding against Story 5.1's own review-found bug (a whole-euro value
  // silently dropping its trailing zeros without minimumFractionDigits).
  const moneyFormat = new Intl.NumberFormat(locale, { minimumFractionDigits: 2, maximumFractionDigits: 2 })
  const kwhFormat = new Intl.NumberFormat(locale, { maximumFractionDigits: 0 })

  return (
    <GlassCard className="flex flex-col gap-4">
      <h2 className="text-lg font-semibold">{t('tariff.compare.heading')}</h2>
      <p className="text-muted-foreground text-sm">{t('tariff.compare.description')}</p>

      <form className="flex flex-col gap-4" onSubmit={handleSubmit}>
        <div className="flex flex-col gap-2">
          <Label htmlFor="tariff-compare-monthly-base-fee">{t('tariff.compare.candidateMonthlyBaseFeeLabel')}</Label>
          <UnitInput
            id="tariff-compare-monthly-base-fee"
            type="number"
            inputMode="decimal"
            unit={currency}
            min="0"
            step="0.01"
            value={candidateMonthlyBaseFee}
            disabled={submitting}
            onChange={(event) => setCandidateMonthlyBaseFee(event.target.value)}
            required
          />
        </div>

        <div className="flex flex-col gap-2">
          <Label htmlFor="tariff-compare-price-per-kwh">{t('tariff.compare.candidatePricePerKwhLabel')}</Label>
          <UnitInput
            id="tariff-compare-price-per-kwh"
            type="number"
            inputMode="decimal"
            unit={`${currency}/kWh`}
            min="0.0001"
            step="0.0001"
            value={candidatePricePerKwh}
            disabled={submitting}
            onChange={(event) => setCandidatePricePerKwh(event.target.value)}
            required
          />
        </div>

        <div className="flex flex-col gap-2">
          <Label htmlFor="tariff-compare-switching-bonus">{t('tariff.compare.candidateSwitchingBonusLabel')}</Label>
          <UnitInput
            id="tariff-compare-switching-bonus"
            type="number"
            inputMode="decimal"
            unit={currency}
            min="0"
            step="0.01"
            value={candidateSwitchingBonus}
            disabled={submitting}
            onChange={(event) => setCandidateSwitchingBonus(event.target.value)}
          />
        </div>

        {error && <p className="text-destructive text-sm">{error}</p>}

        <Button type="submit" variant="glass-primary" disabled={submitting || !isValid} className="self-start">
          {submitting ? t('tariff.compare.comparing') : t('tariff.compare.submit')}
        </Button>
      </form>

      {result === null && (
        // AC #3: the exact same onboarding empty-state markup status-card.tsx uses for its own
        // !status branch — not a differently-worded Tariff-specific state.
        <GlassCard size="lg" className="flex flex-col items-center gap-3 py-6 text-center">
          <p className="text-lg font-bold tracking-[-0.2px]">{t('dashboard.status.emptyTitle')}</p>
          <p className="max-w-[220px] text-sm text-muted-foreground">{t('dashboard.status.emptyBody')}</p>
        </GlassCard>
      )}

      {result && (
        <div className="flex flex-col gap-1">
          <p className="text-base font-semibold tabular-nums">
            {result.bonusNormalizedAnnualSavings >= 0
              ? t('tariff.compare.savingsPositive', {
                  amount: `${moneyFormat.format(result.bonusNormalizedAnnualSavings)} ${result.currency}`,
                })
              : t('tariff.compare.savingsNegative', {
                  amount: `${moneyFormat.format(Math.abs(result.bonusNormalizedAnnualSavings))} ${result.currency}`,
                })}
          </p>
          <p className="text-xs text-muted-foreground">{t('tariff.compare.paceFootnote', { kwh: kwhFormat.format(result.annualPaceKwh) })}</p>
          {result.isLowConfidence && <p className="text-xs text-muted-foreground">{t('tariff.compare.lowConfidenceFootnote')}</p>}
        </div>
      )}
    </GlassCard>
  )
}
