import { apiFetch, toApiError } from '@/lib/api-client'

// A real error response is surfaced to the caller via a thrown ApiError, never silently swallowed.
export interface TariffCheckReminderDto {
  isDue: boolean
  gateOpensAtUtc: string
}

export async function fetchTariffCheckReminder(): Promise<TariffCheckReminderDto | null> {
  const response = await apiFetch('/api/tariff-check')
  if (!response.ok) {
    throw await toApiError(response)
  }
  const text = await response.text()
  return text ? (JSON.parse(text) as TariffCheckReminderDto) : null
}
