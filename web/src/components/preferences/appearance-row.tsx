import { useId } from 'react'
import { Monitor, Moon, Palette, Sun } from 'lucide-react'
import { useTranslation } from 'react-i18next'
import { useThemePreference } from '@/hooks/use-theme-preference'
import type { ThemePreference } from '@/lib/color-scheme'
import { PreferenceRow } from './preference-row'
import { PreferenceStrip } from './preference-strip'

// Story 8.10 (FR-35, UX-DR32): theme override, stored per device by lib/color-scheme.ts.
export function AppearanceRow() {
  const { t } = useTranslation()
  const { preference, resolved, setPreference } = useThemePreference()
  const labelId = useId()

  const resolvedLabel = t(resolved === 'dark' ? 'preferences.appearance.resolvedDark' : 'preferences.appearance.resolvedLight')

  return (
    <PreferenceRow
      icon={<Palette />}
      label={t('preferences.appearance.label')}
      subLabel={t('preferences.appearance.scope')}
      labelId={labelId}
    >
      <PreferenceStrip<ThemePreference>
        ariaLabelledBy={labelId}
        value={preference}
        onChange={setPreference}
        data-testid="appearance-strip"
        options={[
          {
            value: 'system',
            label: t('preferences.appearance.systemCurrently', { value: resolvedLabel }),
            icon: <Monitor className="size-4" aria-hidden="true" />,
          },
          { value: 'light', label: t('preferences.appearance.light'), icon: <Sun className="size-4" aria-hidden="true" /> },
          { value: 'dark', label: t('preferences.appearance.dark'), icon: <Moon className="size-4" aria-hidden="true" /> },
        ]}
      />
    </PreferenceRow>
  )
}
