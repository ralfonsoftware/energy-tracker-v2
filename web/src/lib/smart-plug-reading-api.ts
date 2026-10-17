import { apiFetch, toApiError } from '@/lib/api-client'

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
  const response = await apiFetch('/api/smart-plug-readings')
  if (!response.ok) {
    throw await toApiError(response)
  }

  // Always a JSON array — unlike fetchStatusHistory/fetchCurrentStatus, no empty-body special
  // case is needed here (Results.Ok(list) always writes a real "[]" body, never an empty one).
  return (await response.json()) as RoomMeasuredDataDto[]
}
