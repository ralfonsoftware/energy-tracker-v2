// Same ApiError/toApiError shape as meter-reading-history-api.ts/status-api.ts — copied per API
// file, not shared, per this repo's own established convention.
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

export interface TariffFieldCorrectionDto {
  fieldName: string
  oldValue: string
  newValue: string
  correctedAtUtc: string
}

export interface TariffHistoryItemDto {
  id: string
  monthlyBaseFee: number
  pricePerKwh: number
  currency: string
  contractStartDate: string
  contractPeriodMonths: number
  version: number
  isCurrent: boolean
  effectiveUntil: string | null
  corrections: TariffFieldCorrectionDto[]
}

export interface TariffHistoryPageDto {
  items: TariffHistoryItemDto[]
  totalCount: number
  page: number
  pageSize: number
}

export async function fetchTariffHistory(page: number, pageSize: number): Promise<TariffHistoryPageDto> {
  const response = await fetch(`/api/tariffs?page=${page}&pageSize=${pageSize}`, { credentials: 'include' })
  if (!response.ok) {
    throw await toApiError(response)
  }

  return (await response.json()) as TariffHistoryPageDto
}

export interface CreateTariffInput {
  monthlyBaseFee: number
  pricePerKwh: number
  currency: string
  contractStartDate: string
  contractPeriodMonths: number
}

export async function createTariff(input: CreateTariffInput): Promise<TariffHistoryItemDto> {
  const response = await fetch('/api/tariffs', {
    method: 'POST',
    credentials: 'include',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(input),
  })
  if (!response.ok) {
    throw await toApiError(response)
  }

  return (await response.json()) as TariffHistoryItemDto
}

export interface EditTariffInput {
  monthlyBaseFee?: number
  pricePerKwh?: number
  currency?: string
  contractStartDate?: string
  contractPeriodMonths?: number
  version: number
  overrideConfirmed: boolean
}

export async function updateTariff(id: string, input: EditTariffInput): Promise<TariffHistoryItemDto> {
  const response = await fetch(`/api/tariffs/${id}`, {
    method: 'PUT',
    credentials: 'include',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(input),
  })
  if (!response.ok) {
    throw await toApiError(response)
  }

  return (await response.json()) as TariffHistoryItemDto
}
