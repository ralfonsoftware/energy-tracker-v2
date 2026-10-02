import { useEffect, useId, useRef, useState } from 'react'
import { Globe } from 'lucide-react'
import i18next from 'i18next'
import { useTranslation } from 'react-i18next'
import { useOnlineStatus } from '@/hooks/use-online-status'
import { updateHouseholdLocale } from '@/lib/household-locale-api'
import { useHouseholdLocale } from '@/lib/household-locale-context'
import { PreferenceRow } from './preference-row'
import { PreferenceStrip } from './preference-strip'

type LocaleValue = 'de-DE' | 'en-US'

// Endonyms, never translated and never flags: the literal catalog value is identical in both catalogs.
const ENDONYM_KEY = { 'de-DE': 'preferences.language.option.de', 'en-US': 'preferences.language.option.en' } as const

interface LanguageRowProps {
  householdId: string
  // Writes the post-save announcement into a live region that outlives the menu (see ProfileMenu).
  onAnnounce?: (message: string) => void
  // Story 8.12: lets the Settings card trim the row's horizontal padding; the menu passes nothing.
  rowClassName?: string
}

// Story 8.11 (FR-34, UX-DR32): Household.Locale switch. Reads the persisted Locale and setLocale from
// HouseholdLocaleContext so Story 8.12's Settings card can mount it unchanged.
export function LanguageRow({ householdId, onAnnounce, rowClassName }: LanguageRowProps) {
  const { t } = useTranslation()
  const { locale: persistedLocale, setLocale } = useHouseholdLocale()
  const online = useOnlineStatus()
  const labelId = useId()
  const [pendingLocale, setPendingLocale] = useState<LocaleValue | null>(null)
  const [error, setError] = useState(false)
  const mountedRef = useRef(true)
  const requestIdRef = useRef(0)

  useEffect(() => {
    mountedRef.current = true
    return () => {
      mountedRef.current = false
    }
  }, [])

  const endonym = (locale: string | null) =>
    locale === 'de-DE' || locale === 'en-US' ? t(ENDONYM_KEY[locale]) : (locale ?? '')

  const handleChange = async (next: LocaleValue) => {
    const requestId = ++requestIdRef.current
    setError(false)
    setPendingLocale(next)
    try {
      await updateHouseholdLocale(householdId, next)
      // Context update lives in App and survives unmount, so apply it even if the menu closed.
      setLocale(next)
      onAnnounce?.(i18next.t('preferences.language.announce', { lng: next, language: i18next.t(ENDONYM_KEY[next], { lng: next }) }))
      if (mountedRef.current && requestId === requestIdRef.current) setPendingLocale(null)
    } catch {
      if (mountedRef.current && requestId === requestIdRef.current) {
        setPendingLocale(null)
        setError(true)
      }
    }
  }

  return (
    <div>
      <PreferenceRow
        icon={<Globe />}
        label={t('preferences.language.label')}
        subLabel={online ? t('preferences.language.scope') : t('preferences.language.offline')}
        labelId={labelId}
        className={rowClassName}
      >
        <PreferenceStrip<LocaleValue>
          ariaLabelledBy={labelId}
          value={(pendingLocale ?? persistedLocale ?? '') as LocaleValue}
          pendingValue={pendingLocale ?? undefined}
          onChange={(next) => void handleChange(next)}
          disabled={!online}
          data-testid="language-strip"
          options={[
            { value: 'de-DE', code: 'DE', label: t(ENDONYM_KEY['de-DE']) },
            { value: 'en-US', code: 'EN', label: t(ENDONYM_KEY['en-US']) },
          ]}
        />
      </PreferenceRow>
      {error && (
        <p role="alert" className={`text-destructive ${rowClassName ?? 'px-2'} pb-1 text-[11px] leading-snug`}>
          {t('preferences.language.error', { language: endonym(persistedLocale) })}
        </p>
      )}
    </div>
  )
}
