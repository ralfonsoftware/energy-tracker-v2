import type { ReactNode } from 'react'
import { cn } from '@/lib/utils'

interface PreferenceRowProps {
  icon: ReactNode
  label: string
  subLabel: string
  // id placed on the label element so the strip can reference it via aria-labelledby.
  labelId: string
  children: ReactNode
  className?: string
}

// Story 8.10: label icon + label over a one-line quiet sub-label, strip right-aligned. Shared by the
// Profile menu rows (8.10/8.11) and Settings' Preferences card (8.12).
export function PreferenceRow({ icon, label, subLabel, labelId, children, className }: PreferenceRowProps) {
  return (
    <div className={cn('flex min-h-12 items-center justify-between gap-3 px-2 py-1', className)}>
      <div className="min-w-0">
        <div className="flex items-center gap-1.5 text-foreground">
          <span className="text-muted-foreground [&_svg]:size-3.5" aria-hidden="true">
            {icon}
          </span>
          <span id={labelId} className="text-[12.5px] font-semibold whitespace-nowrap">
            {label}
          </span>
        </div>
        <div className="text-muted-foreground text-[11px] whitespace-nowrap">{subLabel}</div>
      </div>
      {children}
    </div>
  )
}
