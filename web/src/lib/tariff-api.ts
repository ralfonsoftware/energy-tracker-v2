import { apiFetch, toApiError } from '@/lib/api-client'

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
  const response = await apiFetch(`/api/tariffs?page=${page}&pageSize=${pageSize}`)
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
  const response = await apiFetch('/api/tariffs', {
    method: 'POST',
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
  const response = await apiFetch(`/api/tariffs/${id}`, {
    method: 'PUT',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(input),
  })
  if (!response.ok) {
    throw await toApiError(response)
  }

  return (await response.json()) as TariffHistoryItemDto
}

export interface CompareTariffInput {
  candidateMonthlyBaseFee: number
  candidatePricePerKwh: number
  candidateSwitchingBonus: number
}

export interface TariffComparisonDto {
  currentMonthlyBaseFee: number
  currentPricePerKwh: number
  currency: string
  candidateMonthlyBaseFee: number
  candidatePricePerKwh: number
  candidateSwitchingBonus: number
  annualPaceKwh: number
  currentAnnualCost: number
  candidateAnnualCostBonusNormalized: number
  bonusNormalizedAnnualSavings: number
  candidateAnnualCostBonusIncluded: number
  bonusIncludedAnnualSavings: number
  isBonusIncludedWorthSwitching: boolean
  isBonusNormalizedWorthSwitching: boolean
  isLowConfidence: boolean
}

// POST, not GET — matches the backend's own comment (this call performs no write). Empty-body-is-
// null parsing mirrors fetchCurrentStatus (status-api.ts), not this file's own createTariff/
// fetchTariffHistory — this endpoint can legitimately return an empty body (AC #3).
export async function compareTariff(input: CompareTariffInput): Promise<TariffComparisonDto | null> {
  const response = await apiFetch('/api/tariffs/compare', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(input),
  })
  if (!response.ok) {
    throw await toApiError(response)
  }

  const text = await response.text()
  return text ? (JSON.parse(text) as TariffComparisonDto) : null
}
