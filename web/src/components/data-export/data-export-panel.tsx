import { useRef, useState } from 'react'
import { useTranslation } from 'react-i18next'
import { Button } from '@/components/ui/button'
import { GlassCard } from '@/components/ui/glass-card'

// Same ApiError/toApiError shape as ai-plausibility-form.tsx/yearly-baseline-form.tsx — copied per
// component, not shared, per this repo's own established convention.
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

// The server sets Content-Disposition: attachment; filename="..." — read it back so the saved
// file matches the backend's own naming (including its exact export date), falling back to a
// locally-computed name only if the header is ever missing/unparseable. Computed at call time
// (not module-load time) so a long-lived SPA session crossing a date boundary still gets today's
// date in the fallback.
function filenameFromContentDisposition(header: string | null): string {
  const fallback = `energy-tracker-export-${new Date().toISOString().slice(0, 10)}.json`
  if (!header) {
    return fallback
  }
  const match = /filename="?([^";]+)"?/i.exec(header)
  return match?.[1] ?? fallback
}

// Story 7.1: this story only ships the export half (UX-DR12) — no dedicated mockup exists for
// this screen (Dev Notes "No UX mockup for this screen"), built directly against this codebase's
// existing GlassCard/Button conventions instead. Deliberately leaves no control here for import
// yet (Story 7.2's job) but keeps the copy/layout general enough ("Data export & import") for one
// to land later without a rework.
export function DataExportPanel() {
  const { t } = useTranslation()
  const [downloading, setDownloading] = useState(false)
  const [error, setError] = useState<string | null>(null)
  // A ref (not the downloading state) guards against a rapid double-click firing two concurrent
  // requests before React flushes the disabled-button re-render.
  const downloadingRef = useRef(false)

  const handleExport = async () => {
    if (downloadingRef.current) {
      return
    }

    downloadingRef.current = true
    setDownloading(true)
    setError(null)

    try {
      const response = await fetch('/api/household-export', { credentials: 'include' })
      if (!response.ok) {
        throw await toApiError(response)
      }

      const blob = await response.blob()
      const filename = filenameFromContentDisposition(response.headers.get('Content-Disposition'))
      const url = URL.createObjectURL(blob)
      try {
        const link = document.createElement('a')
        link.href = url
        link.download = filename
        document.body.appendChild(link)
        link.click()
        link.remove()
      } finally {
        URL.revokeObjectURL(url)
      }
    } catch (err) {
      if (err instanceof ApiError && err.detail) {
        setError(err.detail)
      } else {
        setError(t('settings.dataExport.errorGeneric'))
      }
    } finally {
      downloadingRef.current = false
      setDownloading(false)
    }
  }

  return (
    <GlassCard className="flex flex-col gap-4">
      <h2 className="text-lg font-semibold">{t('settings.dataExport.heading')}</h2>
      <p className="text-muted-foreground text-sm">{t('settings.dataExport.description')}</p>

      <Button type="button" variant="outline" className="self-start" disabled={downloading} onClick={() => void handleExport()}>
        {downloading ? t('settings.dataExport.exporting') : t('settings.dataExport.trigger')}
      </Button>

      {error && <p className="text-destructive text-sm">{error}</p>}
    </GlassCard>
  )
}
