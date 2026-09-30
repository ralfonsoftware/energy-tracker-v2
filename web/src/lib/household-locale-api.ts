// Story 8.11: persists the Household's Locale. Any non-2xx throws and network errors propagate —
// both are "failure" for the Language row's revert + inline error (AC #4). A 401 is a plain failure.
export async function updateHouseholdLocale(householdId: string, locale: string): Promise<void> {
  const response = await fetch(`/api/households/${householdId}/locale`, {
    method: 'PUT',
    credentials: 'include',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ locale }),
  })
  if (!response.ok) {
    throw new Error(`Request failed with status ${response.status}`)
  }
}
