// Same ApiError/toApiError shape as meter-regression-api.ts — a real error response is surfaced
// to the caller via a thrown ApiError, never silently swallowed.
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

export type StatusValue = 'withinRange' | 'belowBaseline' | 'trending'

export interface StatusDto {
  status: StatusValue
  paceToDateKwh: number
  baselineToDateKwh: number
  isLowConfidence: boolean
}

export interface StatusDetailDto {
  status: StatusValue
  paceToDateKwh: number
  baselineToDateKwh: number
  elapsedDays: number
  trendingThresholdKwh: number
  isLowConfidence: boolean
  daysSinceLastReading: number
  lowConfidenceGapDaysThreshold: number
}

export async function fetchCurrentStatus(): Promise<StatusDto | null> {
  const response = await fetch('/api/status', { credentials: 'include' })
  if (!response.ok) {
    throw await toApiError(response)
  }

  // ASP.NET Core's Results.Ok(null) writes an empty body, not the JSON literal "null" — read as
  // text first and treat empty the same as "null" rather than letting response.json() throw a
  // SyntaxError on an empty body (same precedent as fetchOpenMeterRegressionPrompt).
  const text = await response.text()
  return text ? (JSON.parse(text) as StatusDto) : null
}

export async function fetchStatusDetail(): Promise<StatusDetailDto | null> {
  const response = await fetch('/api/status/detail', { credentials: 'include' })
  if (!response.ok) {
    throw await toApiError(response)
  }

  const text = await response.text()
  return text ? (JSON.parse(text) as StatusDetailDto) : null
}

export interface StatusHistoryEntryDto {
  status: StatusValue
  paceToDateKwh: number
  baselineToDateKwh: number
  isLowConfidence: boolean
  computedAtUtc: string
  gapBeforeThisEntry: boolean
}

export async function fetchStatusHistory(): Promise<StatusHistoryEntryDto[]> {
  const response = await fetch('/api/status/history', { credentials: 'include' })
  if (!response.ok) {
    throw await toApiError(response)
  }

  return (await response.json()) as StatusHistoryEntryDto[]
}
