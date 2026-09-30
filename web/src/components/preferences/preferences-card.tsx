import { useState } from 'react'
import { GlassCard } from '@/components/ui/glass-card'
import { AppearanceRow } from './appearance-row'
import { LanguageRow } from './language-row'

// Story 8.12 (UX-DR33): Settings' Preferences card below 660px, where the Profile menu doesn't exist.
// Composition only — AppearanceRow/LanguageRow own all behavior (Stories 8.10/8.11).
export function PreferencesCard({ householdId }: { householdId: string }) {
  // Same idiom as ProfileMenu: the post-save language announcement needs a live region that is
  // always mounted while the card is.
  const [announcement, setAnnouncement] = useState('')

  return (
    <>
      <div role="status" aria-live="polite" className="sr-only" data-testid="preferences-card-live-region">
        {announcement}
      </div>
      <GlassCard
        data-testid="preferences-card"
        className="gap-0 px-2.5 py-1.5 [&>*+*]:border-t [&>*+*]:border-[rgba(40,70,50,0.08)] dark:[&>*+*]:border-[rgba(210,235,220,0.08)]"
      >
        <AppearanceRow rowClassName="px-0" />
        <LanguageRow householdId={householdId} onAnnounce={setAnnouncement} rowClassName="px-0" />
      </GlassCard>
    </>
  )
}
