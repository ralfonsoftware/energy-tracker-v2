import { useEffect, useState } from 'react'
import { useTranslation } from 'react-i18next'
import { Button } from '@/components/ui/button'
import { GlassCard } from '@/components/ui/glass-card'
import { Label } from '@/components/ui/label'
import { Switch } from '@/components/ui/switch'

interface AiPlausibilitySettings {
  enabled: boolean
  backendConfigured: boolean
  backendLabel: string | null
  version: number
}

interface AiPlausibilityFormProps {
  householdId: string
}

// Same ApiError/toApiError shape as yearly-baseline-form.tsx — copied per component, not shared,
// per this repo's own established convention.
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

// AC #5: always visible regardless of Enabled's value or whether a backend is configured — this
// component never hides itself behind an "advanced" section or a configured-backend check.
export function AiPlausibilityForm({ householdId }: AiPlausibilityFormProps) {
  const { t } = useTranslation()
  const [loading, setLoading] = useState(true)
  const [loadError, setLoadError] = useState(false)
  const [settings, setSettings] = useState<AiPlausibilitySettings | null>(null)
  const [submitting, setSubmitting] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [loadNonce, setLoadNonce] = useState(0)

  useEffect(() => {
    let cancelled = false
    setLoading(true)
    setLoadError(false)

    fetch(`/api/households/${householdId}/ai-plausibility`, { credentials: 'include' })
      .then(async (response) => {
        if (!response.ok) {
          throw new Error(`Unexpected /api/households/${householdId}/ai-plausibility response: ${response.status}`)
        }
        return (await response.json()) as AiPlausibilitySettings
      })
      .then((result) => {
        if (cancelled) {
          return
        }
        setSettings(result)
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

  const handleToggle = async (nextEnabled: boolean) => {
    if (!settings || submitting) {
      return
    }

    setSubmitting(true)
    setError(null)

    try {
      const response = await fetch(`/api/households/${householdId}/ai-plausibility`, {
        method: 'PUT',
        credentials: 'include',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ enabled: nextEnabled, version: settings.version }),
      })

      if (!response.ok) {
        throw await toApiError(response)
      }

      setSettings((await response.json()) as AiPlausibilitySettings)
    } catch (err) {
      if (err instanceof ApiError && err.status === 409) {
        // Don't retry blindly — refetch the current server value/Version and require another tap
        // against the fresh version (NFR10 — no silent overwrite), same pattern as
        // YearlyBaselineForm's own 409 handling.
        let refetched = false
        try {
          const refetchResponse = await fetch(`/api/households/${householdId}/ai-plausibility`, { credentials: 'include' })
          if (refetchResponse.ok) {
            setSettings((await refetchResponse.json()) as AiPlausibilitySettings)
            refetched = true
          }
        } catch {
          // Best-effort refetch — refetched stays false, handled below.
        }
        setError(refetched ? t('settings.aiPlausibility.errorConflict') : t('settings.aiPlausibility.errorConflictRefetchFailed'))
      } else if (err instanceof ApiError && err.detail) {
        setError(err.detail)
      } else {
        setError(t('settings.aiPlausibility.errorGeneric'))
      }
    } finally {
      setSubmitting(false)
    }
  }

  if (loading) {
    return (
      <GlassCard>
        <p className="text-muted-foreground text-sm">{t('settings.aiPlausibility.loading')}</p>
      </GlassCard>
    )
  }

  if (loadError || !settings) {
    return (
      <GlassCard className="flex flex-col items-start gap-2">
        <p className="text-destructive text-sm">{t('settings.aiPlausibility.errorGeneric')}</p>
        <Button type="button" variant="outline" size="sm" onClick={() => setLoadNonce((n) => n + 1)}>
          {t('settings.aiPlausibility.retry')}
        </Button>
      </GlassCard>
    )
  }

  return (
    <GlassCard className="flex flex-col gap-4">
      <h2 className="text-lg font-semibold">{t('settings.aiPlausibility.heading')}</h2>
      <p className="text-muted-foreground text-sm">{t('settings.aiPlausibility.description')}</p>

      <div className="flex items-center justify-between gap-4">
        <Label htmlFor="ai-plausibility-toggle">{t('settings.aiPlausibility.toggleLabel')}</Label>
        <Switch
          id="ai-plausibility-toggle"
          checked={settings.enabled}
          disabled={submitting}
          onCheckedChange={(checked) => void handleToggle(checked)}
        />
      </div>

      <p className="text-muted-foreground text-xs">
        {settings.backendConfigured
          ? t('settings.aiPlausibility.backendConfigured', { label: settings.backendLabel ?? '' })
          : t('settings.aiPlausibility.backendUnconfigured')}
      </p>

      {submitting && <p className="text-muted-foreground text-sm">{t('settings.aiPlausibility.saving')}</p>}
      {error && <p className="text-destructive text-sm">{error}</p>}
    </GlassCard>
  )
}
