import { apiFetch, toApiError } from '@/lib/api-client'

// 'queued' added review-round-2 (Story 3.6): the backend now inserts a Queued BackgroundJob row
// at enqueue time, so GET /api/jobs/{id} returns 200 status:'queued' for a job not yet dequeued
// instead of 404 — use-smart-plug-import-job.ts's own `queued` flag now derives from this instead
// of the (now-defeated) 404-means-queued heuristic.
export type JobStatusValue = 'queued' | 'processing' | 'completed' | 'failed'

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

  const response = await apiFetch('/api/smart-plug-imports', {
    method: 'POST',
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
  const response = await apiFetch(`/api/jobs/${jobId}`)
  if (!response.ok) {
    throw await toApiError(response)
  }

  return (await response.json()) as JobStatusDto
}

// Resolves a Smart Plug import parked 'awaitingpowerpointmapping' by attaching it to an existing
// (or just-created) Power Point (AC #1, #2).
export async function mapSmartPlugImportToPowerPoint(smartPlugImportId: string, powerPointId: string): Promise<void> {
  const response = await apiFetch(`/api/smart-plug-imports/${smartPlugImportId}/power-point-mapping`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ powerPointId }),
  })

  if (!response.ok) {
    throw await toApiError(response)
  }
}

export async function fetchRooms(): Promise<RoomDto[]> {
  const response = await apiFetch('/api/rooms')
  if (!response.ok) {
    throw await toApiError(response)
  }

  return (await response.json()) as RoomDto[]
}

export async function fetchPowerPoints(): Promise<PowerPointDto[]> {
  const response = await apiFetch('/api/power-points')
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
  const response = await apiFetch('/api/smart-plug-import-jobs')
  if (!response.ok) {
    throw await toApiError(response)
  }

  return (await response.json()) as SmartPlugImportJobDto[]
}

// Story 3.10: manual cleanup — deleteAll=false deletes records older than 30 days, deleteAll=true
// deletes everything regardless of age. Unlike fetchSmartPlugImportJobs' own lazy sweep, this is
// eligible across all six states (Waiting/Processing/Needs Mapping included), not just the three
// terminal ones.
//
// Round-3 incident fix (2026-09-12): runs as an async background job (AD-6), same shape as
// uploadSmartPlugFile above — a household's total cleanup work can exceed Azure Container Apps'
// ~240s HTTP ingress ceiling even with every DB command individually bounded, so this can no
// longer execute synchronously. Returns the jobId; the caller polls fetchJobStatus for completion.
export async function cleanUpSmartPlugImportJobs(deleteAll: boolean): Promise<string> {
  const response = await apiFetch(`/api/smart-plug-import-jobs?deleteAll=${deleteAll}`, {
    method: 'DELETE',
  })
  if (!response.ok) {
    throw await toApiError(response)
  }

  const body = (await response.json()) as { jobId: string }
  return body.jobId
}

export async function createPowerPoint(roomId: string, name: string): Promise<PowerPointDto> {
  const response = await apiFetch('/api/power-points', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ roomId, name }),
  })

  if (!response.ok) {
    throw await toApiError(response)
  }

  return (await response.json()) as PowerPointDto
}
