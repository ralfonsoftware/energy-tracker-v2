import { createContext, useContext } from 'react'

export interface HouseholdLocaleValue {
  // Persisted Household.Locale; null outside a ready Household (and in tests without a provider).
  locale: string | null
  // Updates the in-memory Household.Locale after a successful persist so every locale-consuming
  // surface (Intl formatters fed by the `locale` prop) flips together with the UI language.
  setLocale: (locale: string) => void
}

export const HouseholdLocaleContext = createContext<HouseholdLocaleValue>({ locale: null, setLocale: () => {} })

export function useHouseholdLocale(): HouseholdLocaleValue {
  return useContext(HouseholdLocaleContext)
}
