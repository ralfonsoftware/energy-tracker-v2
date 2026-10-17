import { apiFetch, toApiError } from '@/lib/api-client'

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
  const response = await apiFetch('/api/status')
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
  const response = await apiFetch('/api/status/detail')
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
  const response = await apiFetch('/api/status/history')
  if (!response.ok) {
    throw await toApiError(response)
  }

  return (await response.json()) as StatusHistoryEntryDto[]
}
