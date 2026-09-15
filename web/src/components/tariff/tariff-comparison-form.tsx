import { useState, type FormEvent } from 'react'
import { useTranslation } from 'react-i18next'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { GlassCard } from '@/components/ui/glass-card'
import { Label } from '@/components/ui/label'
import { UnitInput } from '@/components/ui/unit-input'
import { cn } from '@/lib/utils'
import { ApiError, compareTariff, type TariffComparisonDto } from '@/lib/tariff-api'

// status-detail-dialog.tsx's established label/value row convention (lines 113-135) — reused
// here for the current-vs-candidate summary panels (AC #4) rather than inventing a new shape.
function SummaryRow({ label, value }: { label: string; value: string }) {
  return (
    <div className="flex items-baseline justify-between gap-4">
      <span className="text-muted-foreground text-sm">{label}</span>
      <span className="text-sm font-semibold tabular-nums">{value}</span>
    </div>
  )
}

// Dedicated FR-13 two-way attractiveness signal pair (never the Status triad, brand-accent, or
// destructive/error red — colors.md's rejection reasoning). Row background/badge/amount-figure
// color are each driven independently by the row's own verdict flag, not a fixed row order
// (DESIGN/components.md: "colors always follow the verdict, not fixed row order") — mirrors
// status-card.tsx's DOT_CLASS/BADGE_CLASS lookup-table pattern via a simple ternary.
function SignalRow({
  isWorthSwitching,
  frameLabel,
  amountSentence,
  detail,
}: {
  isWorthSwitching: boolean
  frameLabel: string
  amountSentence: string
  detail?: string
}) {
  const { t } = useTranslation()
  const rowClass = isWorthSwitching ? 'bg-attractiveness-worth-it-bg' : 'bg-attractiveness-not-worth-it-bg'
  const badgeClass = isWorthSwitching
    ? 'bg-attractiveness-worth-it text-attractiveness-worth-it-badge-text'
    : 'bg-attractiveness-not-worth-it text-attractiveness-not-worth-it-badge-text'
  // The raw --attractiveness-not-worth-it token only clears 3.50:1 as figure text against its own
  // -bg tint (mockup's own verification comment) — the dedicated -text token is used instead;
  // the raw --attractiveness-worth-it token already clears AA there, so it's used directly.
  const amountClass = isWorthSwitching ? 'text-attractiveness-worth-it' : 'text-attractiveness-not-worth-it-text'

  return (
    <div className={cn('flex items-center gap-3 rounded-[14px] p-3', rowClass)}>
      <Badge
        variant="outline"
        className={cn('rounded-full border-0 px-2.5 py-1 text-[10.5px] font-bold tracking-[0.9px] uppercase', badgeClass)}
      >
        {isWorthSwitching ? t('tariff.compare.signal.worthBadge') : t('tariff.compare.signal.notWorthBadge')}
      </Badge>
      <div className="flex-1">
        <p className="text-attractiveness-signal-supporting-text mb-0.5 text-[10px] font-semibold tracking-[0.6px] uppercase">
          {frameLabel}
        </p>
        <p className={cn('text-sm font-bold tabular-nums', amountClass)}>{amountSentence}</p>
        {detail && <p className="text-attractiveness-signal-supporting-text text-[11.5px] leading-snug">{detail}</p>}
      </div>
    </div>
  )
}

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
        <div className="flex flex-col gap-4">
          {/* AC #4: current-vs-candidate tariff summary — stacked glass panels, tabular-nums
              label/value rows (status-detail-dialog.tsx's convention). No tariff names — Tariff.cs
              has no name/label field, so generic headings are used instead of a per-entry name. */}
          <GlassCard className="flex flex-col gap-2">
            <h3 className="text-sm font-semibold">{t('tariff.compare.summary.currentHeading')}</h3>
            <SummaryRow label={t('tariff.form.monthlyBaseFeeLabel')} value={`${moneyFormat.format(result.currentMonthlyBaseFee)} ${result.currency}`} />
            <SummaryRow
              label={t('tariff.form.pricePerKwhLabel')}
              value={`${moneyFormat.format(result.currentPricePerKwh)} ${result.currency}/kWh`}
            />
          </GlassCard>

          <GlassCard className="flex flex-col gap-2">
            <h3 className="text-sm font-semibold">{t('tariff.compare.summary.candidateHeading')}</h3>
            <SummaryRow
              label={t('tariff.compare.candidateMonthlyBaseFeeLabel')}
              value={`${moneyFormat.format(result.candidateMonthlyBaseFee)} ${result.currency}`}
            />
            <SummaryRow
              label={t('tariff.compare.candidatePricePerKwhLabel')}
              value={`${moneyFormat.format(result.candidatePricePerKwh)} ${result.currency}/kWh`}
            />
            {/* Omitted entirely when 0 — showing "€0.00" implies a deliberate zero-bonus candidate
                rather than "nothing was entered" (Task 4). */}
            {result.candidateSwitchingBonus > 0 && (
              <SummaryRow
                label={t('tariff.compare.candidateSwitchingBonusLabel')}
                value={`${moneyFormat.format(result.candidateSwitchingBonus)} ${result.currency}`}
              />
            )}
          </GlassCard>

          {/* AC #1, #2, #3: both rows shown together, always — never toggled. Each row's
              color/badge is driven independently by its own verdict flag (server-computed,
              never re-derived from raw sign-of-savings in the frontend). */}
          <GlassCard className="flex flex-col gap-3">
            <h3 className="text-sm font-semibold">{t('tariff.compare.signal.heading')}</h3>

            <SignalRow
              isWorthSwitching={result.isBonusIncludedWorthSwitching}
              frameLabel={t('tariff.compare.signal.bonusIncludedFrameLabel')}
              amountSentence={
                result.isBonusIncludedWorthSwitching
                  ? t('tariff.compare.signal.bonusIncludedPositive', {
                      amount: `${moneyFormat.format(result.bonusIncludedAnnualSavings)} ${result.currency}`,
                    })
                  : t(
                      // No bonus entered -> "even with the bonus" would misleadingly imply one was applied.
                      result.candidateSwitchingBonus > 0
                        ? 'tariff.compare.signal.bonusIncludedNegative'
                        : 'tariff.compare.signal.bonusIncludedNegativeNoBonus',
                      { amount: `${moneyFormat.format(Math.abs(result.bonusIncludedAnnualSavings))} ${result.currency}` },
                    )
              }
              detail={
                // Skip entirely when there's no bonus — nothing to explain, and the sentence
                // must not falsely claim a bonus was applied (Dev Notes edge case).
                result.candidateSwitchingBonus > 0
                  ? t('tariff.compare.signal.bonusIncludedBonusDetail', {
                      amount: `${moneyFormat.format(result.candidateSwitchingBonus)} ${result.currency}`,
                    })
                  : undefined
              }
            />

            <SignalRow
              isWorthSwitching={result.isBonusNormalizedWorthSwitching}
              frameLabel={t('tariff.compare.signal.bonusNormalizedFrameLabel')}
              amountSentence={
                result.isBonusNormalizedWorthSwitching
                  ? t('tariff.compare.signal.bonusNormalizedPositive', {
                      amount: `${moneyFormat.format(result.bonusNormalizedAnnualSavings)} ${result.currency}`,
                    })
                  : t('tariff.compare.signal.bonusNormalizedNegative', {
                      amount: `${moneyFormat.format(Math.abs(result.bonusNormalizedAnnualSavings))} ${result.currency}`,
                    })
              }
              detail={t('tariff.compare.signal.bonusNormalizedDetail', { kwh: kwhFormat.format(result.annualPaceKwh) })}
            />

            <p className="text-xs text-muted-foreground">{t('tariff.compare.paceFootnote', { kwh: kwhFormat.format(result.annualPaceKwh) })}</p>
            {result.isLowConfidence && <p className="text-xs text-muted-foreground">{t('tariff.compare.lowConfidenceFootnote')}</p>}
          </GlassCard>
        </div>
      )}
    </GlassCard>
  )
}
