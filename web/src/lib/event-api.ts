// Same ApiError/toApiError shape as tariff-api.ts/status-api.ts — copied per API file, not shared,
// per this repo's own established convention.
export class ApiError extends Error {
  status: number
  detail: string | null
  errorCode: string | null

  constructor(status: number, detail: string | null, errorCode: string | null = null) {
    super(`Request failed with status ${status}`)
    this.status = status
    this.detail = detail
    this.errorCode = errorCode
  }
}

async function toApiError(response: Response): Promise<ApiError> {
  try {
    const body = (await response.json()) as { detail?: string; errorCode?: string }
    return new ApiError(response.status, body.detail ?? null, body.errorCode ?? null)
  } catch {
    return new ApiError(response.status, null, null)
  }
}

export interface RoomDto {
  id: string
  name: string
  archivedAt: string | null
}

export interface PowerPointDto {
  id: string
  roomId: string
  name: string
  archivedAt: string | null
}

export interface DeviceDto {
  id: string
  powerPointId: string
  name: string
  archivedAt: string | null
}

export interface TagOption {
  type: 'Room' | 'PowerPoint' | 'Device'
  id: string
  label: string
}

export interface EventDto {
  id: string
  description: string
  occurredAt: string
  taggedEntityType: string | null
  taggedEntityName: string | null
}

export interface CreateEventInput {
  description: string
  occurredAt: string
  taggedEntityType: string | null
  taggedEntityId: string | null
}

export interface EventHistoryPageDto {
  items: EventDto[]
  totalCount: number
  page: number
  pageSize: number
}

export async function createEvent(input: CreateEventInput): Promise<EventDto> {
  // Plain fetch, not attemptSend/the offline queue — Event creation is deliberately not extended
  // into the Meter-Reading-only offline pattern (see the story's Dev Notes).
  const response = await fetch('/api/events', {
    method: 'POST',
    credentials: 'include',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(input),
  })

  if (!response.ok) {
    throw await toApiError(response)
  }

  return (await response.json()) as EventDto
}

export async function fetchEventHistory(page: number, pageSize: number): Promise<EventHistoryPageDto> {
  const response = await fetch(`/api/events?page=${page}&pageSize=${pageSize}`, { credentials: 'include' })
  if (!response.ok) {
    throw await toApiError(response)
  }

  return (await response.json()) as EventHistoryPageDto
}

// Shared by log-event-sheet.tsx and events-card.tsx — both surfaces need identical behavior, so this
// lives in one place rather than being copied per-file the way ApiError/toApiError deliberately are.
// The server's `detail` is English by contract; `errorCode` is what gets localized (AD-18). A 401 is
// called out separately so an expired session doesn't read as a generic failure the user would retry
// forever — matching App.tsx's own 401 handling.
export function messageForEventError(err: unknown, t: (key: string, options?: Record<string, unknown>) => string): string {
  if (err instanceof ApiError) {
    if (err.status === 401) {
      return t('event.errors.sessionExpired')
    }
    if (err.errorCode) {
      return t(`event.errors.${err.errorCode.replace(/^event\./, '')}`, {
        defaultValue: t('event.errorGeneric'),
      })
    }
  }
  return t('event.errorGeneric')
}

/**
 * Builds the flat tag-picker list from the three scaffold endpoints. Only entities that are live
 * *and* whose ancestors are live are offered: ArchiveRoom deliberately does not cascade, so a live
 * Power Point can outlive its Room's archive, and tagging into an already-retired hierarchy is
 * rejected server-side.
 */
export async function fetchTagOptions(): Promise<TagOption[]> {
  const [roomsResponse, powerPointsResponse, devicesResponse] = await Promise.all([
    fetch('/api/rooms', { credentials: 'include' }),
    fetch('/api/power-points', { credentials: 'include' }),
    fetch('/api/devices', { credentials: 'include' }),
  ])

  if (!roomsResponse.ok || !powerPointsResponse.ok || !devicesResponse.ok) {
    throw await toApiError(roomsResponse.ok ? (powerPointsResponse.ok ? devicesResponse : powerPointsResponse) : roomsResponse)
  }

  const [rooms, powerPoints, devices] = await Promise.all([
    roomsResponse.json() as Promise<RoomDto[]>,
    powerPointsResponse.json() as Promise<PowerPointDto[]>,
    devicesResponse.json() as Promise<DeviceDto[]>,
  ])

  const roomsById = new Map(rooms.map((room) => [room.id, room]))
  const powerPointsById = new Map(powerPoints.map((powerPoint) => [powerPoint.id, powerPoint]))

  const isLiveRoom = (roomId: string) => {
    const room = roomsById.get(roomId)
    return room !== undefined && !room.archivedAt
  }

  const livePowerPoints = powerPoints.filter(
    (powerPoint) => !powerPoint.archivedAt && isLiveRoom(powerPoint.roomId),
  )
  const livePowerPointIds = new Set(livePowerPoints.map((powerPoint) => powerPoint.id))

  return [
    ...rooms
      .filter((room) => !room.archivedAt)
      .map((room) => ({ type: 'Room' as const, id: room.id, label: room.name })),
    ...livePowerPoints.map((powerPoint) => ({
      type: 'PowerPoint' as const,
      id: powerPoint.id,
      label: `${roomsById.get(powerPoint.roomId)?.name ?? ''} → ${powerPoint.name}`,
    })),
    ...devices
      .filter((device) => !device.archivedAt && livePowerPointIds.has(device.powerPointId))
      .map((device) => {
        const powerPoint = powerPointsById.get(device.powerPointId)
        const roomName = powerPoint ? (roomsById.get(powerPoint.roomId)?.name ?? '') : ''
        return {
          type: 'Device' as const,
          id: device.id,
          label: `${roomName} → ${powerPoint?.name ?? ''} → ${device.name}`,
        }
      }),
  ]
}
