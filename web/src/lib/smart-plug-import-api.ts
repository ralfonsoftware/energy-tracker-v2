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

export type JobStatusValue = 'processing' | 'completed' | 'failed'

// Matches the backend's lowercased-enum convention already used for `importStatus`.
export type SmartPlugImportGapTreatment = 'estimated' | 'missing' | 'flaggedforreview'

export interface SmartPlugImportGapDto {
  startDate: string
  endDate: string
  treatment: SmartPlugImportGapTreatment
  estimatedTotalKwh: number | null
}

export interface JobStatusDto {
  id: string
  status: JobStatusValue
  // Set only once the job status is 'completed'/'failed' and the job is a Smart Plug import —
  // 'completed' alone doesn't say whether the file fully attached to a Power Point.
  importStatus: string | null
  errorMessage: string | null
  createdAtUtc: string
  completedAtUtc: string | null
  // Set only for a Smart Plug import job — lets the client address the mapping endpoint from a
  // polled job-status response once it resolves to 'awaitingpowerpointmapping'.
  smartPlugImportId: string | null
  // The parsed device tag — the mapping dialog's title and create-Power-Point name prefill.
  smartPlugImportDeviceTag: string | null
  // Empty, never absent, when there's nothing to show (Story 3.3) — simplifies rendering.
  gaps: SmartPlugImportGapDto[]
}

// Same field shapes tagging-scaffold-manager.tsx already uses (camelCase, ASP.NET Core's
// default JSON casing) — kept here rather than shared, since no shared tagging-scaffold API
// client file exists in this codebase yet.
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

// POST confirms immediately (202 Accepted) with a job id — parsing runs asynchronously via the
// job queue (AC #1). The caller learns completion by polling fetchJobStatus, never a callback.
export async function uploadSmartPlugFile(file: File, signal?: AbortSignal): Promise<string> {
  const formData = new FormData()
  formData.append('file', file)

  const response = await fetch('/api/smart-plug-imports', {
    method: 'POST',
    credentials: 'include',
    body: formData,
    signal,
  })

  if (!response.ok) {
    throw await toApiError(response)
  }

  const body = (await response.json()) as { jobId: string }
  return body.jobId
}

export async function fetchJobStatus(jobId: string): Promise<JobStatusDto> {
  const response = await fetch(`/api/jobs/${jobId}`, { credentials: 'include' })
  if (!response.ok) {
    throw await toApiError(response)
  }

  return (await response.json()) as JobStatusDto
}

// Resolves a Smart Plug import parked 'awaitingpowerpointmapping' by attaching it to an existing
// (or just-created) Power Point (AC #1, #2).
export async function mapSmartPlugImportToPowerPoint(smartPlugImportId: string, powerPointId: string): Promise<void> {
  const response = await fetch(`/api/smart-plug-imports/${smartPlugImportId}/power-point-mapping`, {
    method: 'POST',
    credentials: 'include',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ powerPointId }),
  })

  if (!response.ok) {
    throw await toApiError(response)
  }
}

export async function fetchRooms(): Promise<RoomDto[]> {
  const response = await fetch('/api/rooms', { credentials: 'include' })
  if (!response.ok) {
    throw await toApiError(response)
  }

  return (await response.json()) as RoomDto[]
}

export async function fetchPowerPoints(): Promise<PowerPointDto[]> {
  const response = await fetch('/api/power-points', { credentials: 'include' })
  if (!response.ok) {
    throw await toApiError(response)
  }

  return (await response.json()) as PowerPointDto[]
}

// The six states FR-32/UX-DR21 require, never folded into one another or a generic pending/done.
export type SmartPlugImportJobStateValue = 'waiting' | 'processing' | 'success' | 'error' | 'needsMapping' | 'flaggedForReview'

export interface SmartPlugImportJobDto {
  jobId: string
  fileName: string | null
  state: SmartPlugImportJobStateValue
  // null means render a generic fallback — never fabricate a name (UX-DR21).
  queuedByDisplayName: string | null
  queuedAtUtc: string
  completedAtUtc: string | null
  errorMessage: string | null
  smartPlugImportId: string | null
  deviceTag: string | null
  gaps: SmartPlugImportGapDto[]
}

// Story 3.6/FR-32: the household-wide Job Status & History list — every import job any member
// has ever queued, not just the caller's own (AC #1).
export async function fetchSmartPlugImportJobs(): Promise<SmartPlugImportJobDto[]> {
  const response = await fetch('/api/smart-plug-import-jobs', { credentials: 'include' })
  if (!response.ok) {
    throw await toApiError(response)
  }

  return (await response.json()) as SmartPlugImportJobDto[]
}

export async function createPowerPoint(roomId: string, name: string): Promise<PowerPointDto> {
  const response = await fetch('/api/power-points', {
    method: 'POST',
    credentials: 'include',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ roomId, name }),
  })

  if (!response.ok) {
    throw await toApiError(response)
  }

  return (await response.json()) as PowerPointDto
}
