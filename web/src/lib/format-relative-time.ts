// Locale-aware relative time ("2 days ago", "just now") via the native Intl.RelativeTimeFormat —
// matches GapCard.tsx's existing Intl.DateTimeFormat(i18n.language, ...) locale-aware pattern
// (AD-18), never a hardcoded English string or a new npm dependency.
const UNITS: { unit: Intl.RelativeTimeFormatUnit; seconds: number }[] = [
  { unit: 'year', seconds: 31536000 },
  { unit: 'month', seconds: 2592000 },
  { unit: 'week', seconds: 604800 },
  { unit: 'day', seconds: 86400 },
  { unit: 'hour', seconds: 3600 },
  { unit: 'minute', seconds: 60 },
]

export function formatRelativeTime(iso: string, locale: string, now: Date = new Date()): string {
  // Clamped to <= 0: `iso` timestamps are always meant to be in the past, but ordinary clock
  // skew between the server that stamped it and the viewer's own clock can make it come out
  // fractionally ahead — without this, a job queued moments ago would render as "in 5 seconds"
  // instead of "now".
  const deltaSeconds = Math.min(0, (new Date(iso).getTime() - now.getTime()) / 1000)
  const formatter = new Intl.RelativeTimeFormat(locale, { numeric: 'auto' })

  for (const { unit, seconds } of UNITS) {
    if (Math.abs(deltaSeconds) >= seconds) {
      return formatter.format(Math.round(deltaSeconds / seconds), unit)
    }
  }

  return formatter.format(Math.round(deltaSeconds), 'second')
}
