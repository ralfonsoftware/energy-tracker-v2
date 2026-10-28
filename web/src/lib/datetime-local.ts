// datetime-local's value format is local time with no offset (YYYY-MM-DDTHH:mm) — this is what
// both the <input> itself and `new Date(value)` (which interprets it as local time) expect.
export function toDateTimeLocalValue(date: Date): string {
  const pad = (n: number) => String(n).padStart(2, '0')
  return `${date.getFullYear()}-${pad(date.getMonth() + 1)}-${pad(date.getDate())}T${pad(date.getHours())}:${pad(date.getMinutes())}`
}
