import { useEffect, useState, type FormEvent } from 'react'
import { useTranslation } from 'react-i18next'
import { Button } from '@/components/ui/button'
import { GlassCard } from '@/components/ui/glass-card'
import { Label } from '@/components/ui/label'
import { UnitInput } from '@/components/ui/unit-input'
import { HouseholdSizePresetRow, PRESETS } from './household-size-preset-row'

interface HouseholdDetails {
  id: string
  locale: string
  currency: string
  yearlyBaselineKwh: number | null
  version: number
}

interface YearlyBaselineFormProps {
  householdId: string
}

class ApiError extends Error {
  status: number
  detail: string | null

  constructor(status: number, detail: string | null) {
    super(`Request failed with status ${status}`)
    this.status = status
    this.detail = detail
  }
}

async function toApiError(response: Response): Promise<ApiError> {
  try {
    const body = (await response.json()) as { detail?: string }
    return new ApiError(response.status, body.detail ?? null)
  } catch {
    return new ApiError(response.status, null)
  }
}

// Mirrors SetYearlyBaseline's server-side MaxYearlyBaselineKwh — keeps the value inside the
// decimal(18,2) column's range and rejects an Infinity-producing input before it ever reaches
// JSON.stringify (which would otherwise silently serialize Infinity as null).
const MAX_KWH = 1_000_000

export function YearlyBaselineForm({ householdId }: YearlyBaselineFormProps) {
  const { t } = useTranslation()
  const [loading, setLoading] = useState(true)
  const [loadError, setLoadError] = useState(false)
  const [input, setInput] = useState('')
  const [version, setVersion] = useState(0)
  const [submitting, setSubmitting] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [loadNonce, setLoadNonce] = useState(0)

  useEffect(() => {
    let cancelled = false
    setLoading(true)
    setLoadError(false)

    fetch(`/api/households/${householdId}`, { credentials: 'include' })
      .then(async (response) => {
        if (!response.ok) {
          throw new Error(`Unexpected /api/households/${householdId} response: ${response.status}`)
        }
        return (await response.json()) as HouseholdDetails
      })
      .then((details) => {
        if (cancelled) {
          return
        }
        setInput(details.yearlyBaselineKwh !== null ? String(details.yearlyBaselineKwh) : '')
        setVersion(details.version)
      })
      .catch(() => {
        if (!cancelled) {
          setLoadError(true)
        }
      })
      .finally(() => {
        if (!cancelled) {
          setLoading(false)
        }
      })

    return () => {
      cancelled = true
    }
  }, [householdId, loadNonce])

  const handleSubmit = async (event: FormEvent<HTMLFormElement>) => {
    event.preventDefault()
    const yearlyBaselineKwh = Number(input)
    if (!input || !Number.isFinite(yearlyBaselineKwh) || yearlyBaselineKwh <= 0 || yearlyBaselineKwh > MAX_KWH) {
      return
    }

    setSubmitting(true)
    setError(null)

    try {
      const response = await fetch(`/api/households/${householdId}/yearly-baseline`, {
        method: 'PUT',
        credentials: 'include',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ yearlyBaselineKwh, version }),
      })

      if (!response.ok) {
        throw await toApiError(response)
      }

      const details = (await response.json()) as HouseholdDetails
      setInput(details.yearlyBaselineKwh !== null ? String(details.yearlyBaselineKwh) : '')
      setVersion(details.version)
    } catch (err) {
      if (err instanceof ApiError && err.status === 409) {
        // Don't retry blindly — refetch the current server value/Version and require the user to
        // resubmit against the fresh version (NFR10 — no silent overwrite). Only claim "we've
        // loaded the latest value" when the refetch actually succeeded; otherwise the displayed
        // value is still stale and the message must say so instead.
        let refetched = false
        try {
          const refetchResponse = await fetch(`/api/households/${householdId}`, { credentials: 'include' })
          if (refetchResponse.ok) {
            const details = (await refetchResponse.json()) as HouseholdDetails
            setInput(details.yearlyBaselineKwh !== null ? String(details.yearlyBaselineKwh) : '')
            setVersion(details.version)
            refetched = true
          }
        } catch {
          // Best-effort refetch — refetched stays false, handled below.
        }
        setError(refetched ? t('yearlyBaseline.errorConflict') : t('yearlyBaseline.errorConflictRefetchFailed'))
      } else if (err instanceof ApiError && err.detail) {
        setError(err.detail)
      } else {
        setError(t('yearlyBaseline.errorGeneric'))
      }
    } finally {
      setSubmitting(false)
    }
  }

  if (loading) {
    return (
      <GlassCard>
        <p className="text-muted-foreground text-sm">{t('yearlyBaseline.loading')}</p>
      </GlassCard>
    )
  }

  if (loadError) {
    return (
      <GlassCard className="flex flex-col items-start gap-2">
        <p className="text-destructive text-sm">{t('yearlyBaseline.errorGeneric')}</p>
        <Button type="button" variant="outline" size="sm" onClick={() => setLoadNonce((n) => n + 1)}>
          {t('yearlyBaseline.retry')}
        </Button>
      </GlassCard>
    )
  }

  return (
    <GlassCard className="flex flex-col gap-4">
      <h2 className="text-lg font-semibold">{t('yearlyBaseline.heading')}</h2>
      <p className="text-muted-foreground text-sm">{t('yearlyBaseline.description')}</p>

      <HouseholdSizePresetRow
        presets={PRESETS}
        selectedKwh={input ? Number(input) : null}
        onSelect={(kwh) => setInput(String(kwh))}
      />

      <form className="flex flex-col gap-2" onSubmit={handleSubmit}>
        <Label htmlFor="yearly-baseline-input">{t('yearlyBaseline.inputLabel')}</Label>
        <UnitInput
          id="yearly-baseline-input"
          type="number"
          unit="kWh"
          min="0"
          max={MAX_KWH}
          step="1"
          value={input}
          disabled={submitting}
          onChange={(event) => setInput(event.target.value)}
          required
        />

        {error && <p className="text-destructive text-sm">{error}</p>}

        <Button type="submit" variant="glass-primary" disabled={submitting || !input} className="self-start">
          {submitting ? t('yearlyBaseline.saving') : t('yearlyBaseline.submit')}
        </Button>
      </form>
    </GlassCard>
  )
}
