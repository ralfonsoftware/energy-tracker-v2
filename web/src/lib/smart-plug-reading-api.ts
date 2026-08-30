// Same ApiError/toApiError shape as status-api.ts — a real error response is surfaced to the
// caller via a thrown ApiError, never silently swallowed.
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

export interface DeviceMeasuredDataDto {
  deviceName: string
  totalKwh: number
}

export interface PowerPointMeasuredDataDto {
  powerPointName: string
  totalKwh: number
  devices: DeviceMeasuredDataDto[]
}

export interface RoomMeasuredDataDto {
  roomName: string
  totalKwh: number
  powerPoints: PowerPointMeasuredDataDto[]
}

export async function fetchPerPlugMeasuredData(): Promise<RoomMeasuredDataDto[]> {
  const response = await fetch('/api/smart-plug-readings', { credentials: 'include' })
  if (!response.ok) {
    throw await toApiError(response)
  }

  // Always a JSON array — unlike fetchStatusHistory/fetchCurrentStatus, no empty-body special
  // case is needed here (Results.Ok(list) always writes a real "[]" body, never an empty one).
  return (await response.json()) as RoomMeasuredDataDto[]
}
