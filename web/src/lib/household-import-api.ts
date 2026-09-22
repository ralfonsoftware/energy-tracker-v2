// Same ApiError/toApiError shape as data-export-panel.tsx/smart-plug-import-api.ts — copied per
// module, not shared, per this repo's own established convention.
export class ApiError extends Error {
  status: number
  detail: string | null

  constructor(status: number, detail: string | null) {
    super(`Request failed with status ${status}`)
    this.status = status
    this.detail = detail
  }
}

// UX-DR14's "Import data fails validation" state: distinct from ApiError so the panel can render
// the full list of what failed, not just a single detail string.
export class HouseholdImportValidationError extends Error {
  failures: string[]

  constructor(failures: string[]) {
    super('The uploaded file failed validation.')
    this.failures = failures
  }
}

export interface HouseholdImportSummary {
  householdMembers: number
  hasMainMeter: boolean
  meterReadings: number
  meterRegressionPrompts: number
  tariffs: number
  events: number
  rooms: number
  powerPoints: number
  devices: number
  smartPlugReadings: number
  statusSnapshots: number
  auditCorrections: number
}

export interface HouseholdImportValidationResult {
  token: string
  summary: HouseholdImportSummary
}

// POST /api/household-import: synchronous upload + structural validation, no DB write of any
// kind (AC #1, #2). On success, the server holds the file server-side keyed by the returned
// opaque token — the client never re-uploads it for the confirm step below.
export async function uploadHouseholdImportFile(file: File, signal?: AbortSignal): Promise<HouseholdImportValidationResult> {
  const formData = new FormData()
  formData.append('file', file)

  const response = await fetch('/api/household-import', {
    method: 'POST',
    credentials: 'include',
    body: formData,
    signal,
  })

  if (!response.ok) {
    // A Response body can only be read once — reading it here to check for `failures` and then
    // falling through to toApiError's own response.json() call on the same, already-consumed
    // Response silently threw inside toApiError's try/catch, discarding the server's real detail
    // message for any 400 that isn't shaped as the canonical failures-array payload (e.g. the
    // file-too-large 400) — code review, Story 7.2 Pass 2. Read the body exactly once here
    // instead, and branch on its shape.
    if (response.status === 400) {
      const body = (await response.json().catch(() => null)) as { detail?: string; failures?: string[] } | null
      if (body?.failures && body.failures.length > 0) {
        throw new HouseholdImportValidationError(body.failures)
      }

      throw new ApiError(response.status, body?.detail ?? null)
    }

    throw await toApiError(response)
  }

  return (await response.json()) as HouseholdImportValidationResult
}

// POST /api/household-import/{token}/confirm: the explicit "replace all data" confirmation (AC
// #4) — enqueues the actual restore as an async background job and returns immediately.
export async function confirmHouseholdImport(token: string, signal?: AbortSignal): Promise<string> {
  const response = await fetch(`/api/household-import/${token}/confirm`, {
    method: 'POST',
    credentials: 'include',
    signal,
  })

  if (!response.ok) {
    throw await toApiError(response)
  }

  const body = (await response.json()) as { jobId: string }
  return body.jobId
}

async function toApiError(response: Response): Promise<ApiError> {
  try {
    const body = (await response.json()) as { detail?: string }
    return new ApiError(response.status, body.detail ?? null)
  } catch {
    return new ApiError(response.status, null)
  }
}
