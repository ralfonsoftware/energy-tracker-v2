import { apiFetch, toApiError } from '@/lib/api-client'

export interface MeterReadingHistoryItemDto {
  id: string
  kwhValue: number
  readingTimestamp: string
  version: number
  isPendingRegression: boolean
  correctedFromKwhValue: number | null
  correctedAtUtc: string | null
}

export interface MeterReadingHistoryPageDto {
  items: MeterReadingHistoryItemDto[]
  totalCount: number
  page: number
  pageSize: number
}

export async function fetchMeterReadingHistory(page: number, pageSize: number): Promise<MeterReadingHistoryPageDto> {
  const response = await apiFetch(`/api/meter-readings?page=${page}&pageSize=${pageSize}`)
  if (!response.ok) {
    throw await toApiError(response)
  }

  return (await response.json()) as MeterReadingHistoryPageDto
}

export async function updateMeterReading(id: string, kwhValue: number, version: number): Promise<MeterReadingHistoryItemDto> {
  const response = await apiFetch(`/api/meter-readings/${id}`, {
    method: 'PUT',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ kwhValue, version }),
  })
  if (!response.ok) {
    throw await toApiError(response)
  }

  return (await response.json()) as MeterReadingHistoryItemDto
}
