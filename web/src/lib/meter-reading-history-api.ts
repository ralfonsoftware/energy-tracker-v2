// Same ApiError/toApiError shape as status-api.ts/meter-regression-api.ts — a real error response
// is surfaced to the caller via a thrown ApiError, never silently swallowed.
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
  const response = await fetch(`/api/meter-readings?page=${page}&pageSize=${pageSize}`, { credentials: 'include' })
  if (!response.ok) {
    throw await toApiError(response)
  }

  return (await response.json()) as MeterReadingHistoryPageDto
}

export async function updateMeterReading(id: string, kwhValue: number, version: number): Promise<MeterReadingHistoryItemDto> {
  const response = await fetch(`/api/meter-readings/${id}`, {
    method: 'PUT',
    credentials: 'include',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ kwhValue, version }),
  })
  if (!response.ok) {
    throw await toApiError(response)
  }

  return (await response.json()) as MeterReadingHistoryItemDto
}
