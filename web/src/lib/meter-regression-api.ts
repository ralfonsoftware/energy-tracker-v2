// Same ApiError/toApiError shape as meter-reading-sync.ts — a real error response is surfaced to
// the caller via a thrown ApiError, never silently swallowed.
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

export interface MeterRegressionPromptDto {
  id: string
  meterReadingId: string
  readingKwhValue: number
  readingTimestamp: string
  previousMeterReadingId: string
  previousReadingKwhValue: number
  previousReadingTimestamp: string
  mainMeterDigitCapacityKwh: number | null
}

export type MeterRegressionClassification = 'reset' | 'rollover'

export async function fetchOpenMeterRegressionPrompt(): Promise<MeterRegressionPromptDto | null> {
  const response = await fetch('/api/meter-regression-prompts/open', { credentials: 'include' })
  if (!response.ok) {
    throw await toApiError(response)
  }

  // ASP.NET Core's Results.Ok(null) writes an empty body, not the JSON literal "null" — read as
  // text first and treat empty the same as "null" rather than letting response.json() throw a
  // SyntaxError on an empty body.
  const text = await response.text()
  return text ? (JSON.parse(text) as MeterRegressionPromptDto) : null
}

export async function resolveMeterRegressionPrompt(
  id: string,
  classification: MeterRegressionClassification,
  digitCapacityKwh?: number,
): Promise<void> {
  const response = await fetch(`/api/meter-regression-prompts/${id}/resolve`, {
    method: 'POST',
    credentials: 'include',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ classification, digitCapacityKwh: digitCapacityKwh ?? null }),
  })

  if (!response.ok) {
    throw await toApiError(response)
  }
}
