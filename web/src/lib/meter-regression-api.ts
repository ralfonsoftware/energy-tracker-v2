import { apiFetch, toApiError } from '@/lib/api-client'

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
  const response = await apiFetch('/api/meter-regression-prompts/open')
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
  const response = await apiFetch(`/api/meter-regression-prompts/${id}/resolve`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ classification, digitCapacityKwh: digitCapacityKwh ?? null }),
  })

  if (!response.ok) {
    throw await toApiError(response)
  }
}
