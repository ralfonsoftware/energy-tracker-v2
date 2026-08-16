import { useState, type FormEvent } from 'react'
import { useTranslation } from 'react-i18next'
import { Button } from '@/components/ui/button'
import { GlassCard } from '@/components/ui/glass-card'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import {
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
} from '@/components/ui/select'

// Launch Locales only (AD-15/NFR5) — matches CreateHousehold.SupportedLocales server-side.
const SUPPORTED_LOCALES = ['de-DE', 'en-US'] as const
type SupportedLocale = (typeof SUPPORTED_LOCALES)[number]

// Suggested starting value tied to the picked Locale, same "suggestion the user must still
// confirm, never silently applied" pattern AD-15 establishes for Yearly Baseline presets.
const SUGGESTED_CURRENCY: Record<SupportedLocale, string> = {
  'de-DE': 'EUR',
  'en-US': 'USD',
}

export interface CreatedHousehold {
  id: string
  locale: string
  currency: string
}

interface HouseholdCreationFormProps {
  onCreated: (household: CreatedHousehold) => void
}

export function HouseholdCreationForm({ onCreated }: HouseholdCreationFormProps) {
  const { t } = useTranslation()
  const [locale, setLocale] = useState<SupportedLocale | ''>('')
  const [currency, setCurrency] = useState('')
  const [submitting, setSubmitting] = useState(false)
  const [error, setError] = useState<string | null>(null)

  const handleLocaleChange = (value: string) => {
    const nextLocale = value as SupportedLocale
    setLocale(nextLocale)
    // Only pre-fills an empty field — never overwrites something the user already typed.
    setCurrency((current) => (current === '' ? SUGGESTED_CURRENCY[nextLocale] : current))
  }

  const handleSubmit = async (event: FormEvent<HTMLFormElement>) => {
    event.preventDefault()
    if (!locale || !currency) {
      return
    }

    setSubmitting(true)
    setError(null)

    try {
      const response = await fetch('/api/households', {
        method: 'POST',
        credentials: 'include',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ locale, currency }),
      })

      if (response.status === 409) {
        // The authenticated principal already has a Household (e.g. a second tab/duplicate
        // submit that raced this one) — reload so the app re-fetches /api/session and routes to
        // the real state, instead of leaving this form stuck showing a dead-end generic error.
        window.location.reload()
        return
      }

      if (!response.ok) {
        throw new Error(`Unexpected /api/households response: ${response.status}`)
      }

      const household = (await response.json()) as CreatedHousehold
      onCreated(household)
    } catch {
      setError(t('householdCreation.errorGeneric'))
      setSubmitting(false)
    }
  }

  return (
    <main className="flex min-h-svh flex-col items-center justify-center gap-6 p-4">
      <div className="flex w-full max-w-sm flex-col gap-6">
        <div className="flex flex-col gap-1 text-center">
          <h1 className="text-2xl font-semibold">{t('householdCreation.heading')}</h1>
          <p className="text-muted-foreground text-sm">{t('householdCreation.description')}</p>
        </div>

        <GlassCard>
          <form className="flex flex-col gap-4" onSubmit={handleSubmit}>
            <div className="flex flex-col gap-2">
              <Label htmlFor="household-locale">{t('householdCreation.localeLabel')}</Label>
              <Select value={locale} onValueChange={handleLocaleChange}>
                <SelectTrigger id="household-locale">
                  <SelectValue placeholder={t('householdCreation.localeLabel')} />
                </SelectTrigger>
                <SelectContent>
                  {SUPPORTED_LOCALES.map((supportedLocale) => (
                    <SelectItem key={supportedLocale} value={supportedLocale}>
                      {t(`householdCreation.localeOption.${supportedLocale}`)}
                    </SelectItem>
                  ))}
                </SelectContent>
              </Select>
            </div>

            <div className="flex flex-col gap-2">
              <Label htmlFor="household-currency">{t('householdCreation.currencyLabel')}</Label>
              <Input
                id="household-currency"
                value={currency}
                onChange={(event) => setCurrency(event.target.value.toUpperCase())}
                placeholder={t('householdCreation.currencyPlaceholder')}
                maxLength={3}
                required
              />
            </div>

            {error && <p className="text-destructive text-sm">{error}</p>}

            <Button type="submit" variant="glass-primary" disabled={submitting || !locale || !currency}>
              {submitting ? t('householdCreation.submitting') : t('householdCreation.submit')}
            </Button>
          </form>
        </GlassCard>
      </div>
    </main>
  )
}
