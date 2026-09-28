import type { ReactNode } from 'react'

// Replaces each section's own <h2> at >=660px (Story 8.5, Task 2) — see settings-page.tsx's
// callers. Must stay an <h2>, not a <div>: it's standing in for a real heading, and downgrading
// heading semantics at the wide breakpoint would be an accessibility regression.
export function SectionLabel({ children }: { children: ReactNode }) {
  return (
    <h2 className="hidden wide:block text-[11px] font-bold tracking-[0.6px] text-muted-foreground uppercase">
      {children}
    </h2>
  )
}
