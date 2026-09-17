import { useTranslation } from 'react-i18next'
import type { TariffCheckReminderDto } from '@/lib/tariff-check-api'

interface TariffCheckCardProps {
  reminder: TariffCheckReminderDto | null
  locale: string
  onClick?: () => void
}

// FR-15/UX-DR5 quiet prompt card. Plain div, no GlassCard — components.md is explicit this
// surface has no glass-blur, no glow, no border emphasis, deliberately lower visual weight than
// every other card in the product. Renders nothing when reminder is null (no Tariff configured
// yet, resolved ambiguity #2 in story 5.4's Scope Reality Check).
export function TariffCheckCard({ reminder, locale, onClick }: TariffCheckCardProps) {
  const { t } = useTranslation()

  if (!reminder) {
    return null
  }

  const dateFormat = new Intl.DateTimeFormat(locale, { dateStyle: 'medium' })
  const text = reminder.isDue
    ? t('tariffCheck.due')
    : t('tariffCheck.notDue', { date: dateFormat.format(new Date(reminder.gateOpensAtUtc)) })

  const className = 'w-full rounded-2xl border bg-tariff-check-card-bg border-tariff-check-card-border p-4 text-left text-sm text-muted-foreground'

  if (onClick) {
    return (
      <button
        type="button"
        onClick={onClick}
        aria-label={t('tariffCheck.openLabel')}
        className={`${className} outline-none transition-colors hover:text-foreground focus-visible:border-ring focus-visible:ring-3 focus-visible:ring-ring/50`}
      >
        {text}
      </button>
    )
  }

  return <div className={className}>{text}</div>
}
