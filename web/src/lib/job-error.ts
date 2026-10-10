import type { TFunction } from 'i18next'

// Stable failure codes the backend writes into a failed job's `errorMessage` (AD-6/AD-18,
// Story 11.2). The backend has no concept of the client's locale, so it sends the code and the
// client renders its own sentence. Recognised by exact equality only: a validation sentence or any
// other message is shown verbatim, exactly as before, and a raw code is never shown to a member.
const CODE_KEYS: Record<string, string> = {
  'job-interrupted': 'jobError.interrupted',
  'job-retries-exhausted': 'jobError.retriesExhausted',
  'upload-missing': 'jobError.uploadMissing',
}

export function translateJobError(t: TFunction, errorMessage: string | null | undefined, fallback: string): string {
  if (!errorMessage) {
    return fallback
  }

  const key = Object.hasOwn(CODE_KEYS, errorMessage) ? CODE_KEYS[errorMessage] : undefined
  return key ? t(key) : errorMessage
}
