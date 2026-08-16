import { User } from 'lucide-react'
import { useTranslation } from 'react-i18next'
import { cn } from '@/lib/utils'

// Household-size presets (AD-15) — starting suggestions only. Clicking one only fills the field,
// it never auto-applies or auto-submits (Story 2.1 AC #1). Plain frontend constants: there is no
// backend endpoint for these, only the chosen final value is persisted. The kWh number is
// interpolated into the translation string (`{{kwh}}`) rather than hand-duplicated per locale, so
// this array stays the single source of truth.
export const PRESETS = [
  { key: 'preset1', kwh: 1500, count: 1 },
  { key: 'preset2', kwh: 2500, count: 2 },
  { key: 'preset3', kwh: 3500, count: 3 },
  { key: 'preset4', kwh: 4250, count: 4 },
] as const

type Preset = (typeof PRESETS)[number]

interface HouseholdSizePresetRowProps {
  presets: readonly Preset[]
  selectedKwh: number | null
  onSelect: (kwh: number) => void
}

export function HouseholdSizePresetRow({ presets, selectedKwh, onSelect }: HouseholdSizePresetRowProps) {
  const { t } = useTranslation()

  return (
    <div className="flex gap-2">
      {presets.map((preset) => {
        const active = selectedKwh === preset.kwh
        return (
          <button
            key={preset.key}
            type="button"
            aria-label={t(`yearlyBaseline.${preset.key}`, { kwh: preset.kwh })}
            aria-pressed={active}
            onClick={() => onSelect(preset.kwh)}
            className={cn(
              'relative flex flex-1 flex-col items-center gap-1 rounded-glass-sm border px-1 py-2.5',
              active
                ? 'border-[rgba(107,134,86,0.4)] bg-[rgba(107,134,86,0.16)] dark:border-[rgba(159,187,138,0.4)] dark:bg-[rgba(159,187,138,0.16)]'
                : 'border-[rgba(40,70,50,0.14)] bg-[rgba(255,255,255,0.5)] dark:border-[rgba(210,235,220,0.14)] dark:bg-[rgba(220,245,230,0.05)]'
            )}
          >
            <span className="relative">
              <User
                className={cn(
                  'size-[22px]',
                  active
                    ? 'stroke-[#41603A] dark:stroke-[#C7DCBB]'
                    : 'stroke-[rgba(30,42,28,0.6)] dark:stroke-[rgba(234,245,238,0.55)]'
                )}
                strokeWidth={2}
              />
              <span
                aria-hidden="true"
                className={cn(
                  'absolute -top-1 -right-1.5 flex h-3.5 min-w-3.5 items-center justify-center rounded-full px-0.5 text-[8.5px] leading-none font-bold',
                  active
                    ? 'bg-[#41603A] text-white dark:bg-[#C7DCBB] dark:text-[#16210F]'
                    : 'bg-[rgba(255,255,255,0.85)] text-[rgba(30,42,28,0.7)] dark:bg-[rgba(8,16,12,0.65)] dark:text-[rgba(234,245,238,0.65)]'
                )}
              >
                {preset.count}
              </span>
            </span>
            <span
              className={cn(
                'text-[10px] font-bold tabular-nums',
                active ? 'text-[#41603A] dark:text-[#C7DCBB]' : 'text-[rgba(30,42,28,0.65)] dark:text-[rgba(234,245,238,0.6)]'
              )}
            >
              {preset.kwh}
            </span>
          </button>
        )
      })}
    </div>
  )
}
