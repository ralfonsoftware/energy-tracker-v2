// Story 11.8 (AC #2; AD-27, audit S9/R2): the one place the SPA talks to fetch. It adds credentials and the
// X-Requested-With header the API requires on every state-changing request, and owns ApiError/toApiError.
// It returns the raw Response and never throws on a non-2xx status: call sites branch on `status`
// themselves. No retry, no redirect-on-401, no JSON helpers. Stores nothing (FrontendDoesNotStoreAuthTokensTests).
export const REQUESTED_WITH_HEADER = 'X-Requested-With'
export const REQUESTED_WITH_VALUE = 'fetch'

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

export async function toApiError(response: Response): Promise<ApiError> {
  try {
    const body = (await response.json()) as { detail?: string; errorCode?: string }
    return new ApiError(response.status, body.detail ?? null, body.errorCode ?? null)
  } catch {
    return new ApiError(response.status, null, null)
  }
}

// Never sets Content-Type: a FormData body needs the browser's multipart boundary.
export function apiFetch(input: string, init: RequestInit = {}): Promise<Response> {
  const headers = new Headers(init.headers)
  headers.set(REQUESTED_WITH_HEADER, REQUESTED_WITH_VALUE)
  return fetch(input, { ...init, credentials: 'include', headers })
}
