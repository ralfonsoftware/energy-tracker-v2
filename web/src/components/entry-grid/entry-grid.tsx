import type { ReactNode } from 'react'
import { cn } from '@/lib/utils'

// Only rendered at >=660px (gated by useWideBreakpoint); column count falls out of auto-fill/minmax.
export function EntryGrid({ children, className }: { children: ReactNode; className?: string }) {
  return (
    <ul role="list" data-slot="entry-grid" className={cn('grid grid-cols-[repeat(auto-fill,minmax(260px,1fr))] gap-3', className)}>
      {children}
    </ul>
  )
}

interface EntryTileProps {
  children: ReactNode
  actions?: ReactNode
}

export function EntryTile({ children, actions }: EntryTileProps) {
  return (
    <li className="min-w-0 rounded-glass-sm bg-surface-quiet break-words border-surface-quiet-border flex flex-col gap-[3px] border px-[15px] pt-[13px] pb-[12px]">
      {children}
      {actions && <div className="mt-2 flex justify-end">{actions}</div>}
    </li>
  )
}
