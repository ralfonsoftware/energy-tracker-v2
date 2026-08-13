import i18next from 'i18next'
import LanguageDetector from 'i18next-browser-languagedetector'
import { initReactI18next } from 'react-i18next'
import deDE from '@/locales/de-DE/translation.json'
import enUS from '@/locales/en-US/translation.json'

// Additive catalogs (AD-18) — a later Locale is a new resources entry + JSON file, never a code change.
export const supportedLocales = ['de-DE', 'en-US'] as const

// Before a Household exists there is no Household.Locale to read yet, so the browser-detected
// language is used purely as the *display* language for this screen's own chrome, falling back
// to en-US if undetected/unsupported. The Locale value the household member actually submits on
// the creation form is always their own explicit selection — this detector never drives that.
void i18next
  .use(LanguageDetector)
  .use(initReactI18next)
  .init({
    resources: {
      'de-DE': { translation: deDE },
      'en-US': { translation: enUS },
    },
    supportedLngs: supportedLocales,
    fallbackLng: 'en-US',
    interpolation: {
      escapeValue: false,
    },
  })

export default i18next
