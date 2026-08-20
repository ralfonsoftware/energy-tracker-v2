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

export interface JobStatusDto {
  id: string
  status: JobStatusValue
  // Set only once the job status is 'completed'/'failed' and the job is a Smart Plug import —
  // 'completed' alone doesn't say whether the file fully attached to a Power Point.
  importStatus: string | null
  errorMessage: string | null
  createdAtUtc: string
  completedAtUtc: string | null
}

// POST confirms immediately (202 Accepted) with a job id — parsing runs asynchronously via the
// job queue (AC #1). The caller learns completion by polling fetchJobStatus, never a callback.
export async function uploadSmartPlugFile(file: File): Promise<string> {
  const formData = new FormData()
  formData.append('file', file)

  const response = await fetch('/api/smart-plug-imports', {
    method: 'POST',
    credentials: 'include',
    body: formData,
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
