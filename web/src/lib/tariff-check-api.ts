// Same ApiError/toApiError shape as status-api.ts/meter-regression-api.ts — a real error response
// is surfaced to the caller via a thrown ApiError, never silently swallowed. Duplicated per-file
// rather than extracted into a shared module — the established convention in this codebase.
export class ApiError extends Error {
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

export interface TariffCheckReminderDto {
  isDue: boolean
  gateOpensAtUtc: string
}

export async function fetchTariffCheckReminder(): Promise<TariffCheckReminderDto | null> {
  const response = await fetch('/api/tariff-check', { credentials: 'include' })
  if (!response.ok) {
    throw await toApiError(response)
  }
  const text = await response.text()
  return text ? (JSON.parse(text) as TariffCheckReminderDto) : null
}
