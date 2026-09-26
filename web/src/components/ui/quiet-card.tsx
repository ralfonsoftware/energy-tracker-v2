import * as React from "react"

import { cn } from "@/lib/utils"
import { Card } from "@/components/ui/card"

// Epic 8 UX-DR24 "quiet" surface tier (DESIGN/colors.md) — near-transparent, no backdrop blur,
// no border emphasis, for pure-reference-display cards with no interactive controls. Reuses
// GlassCard's shape (rounded-glass-md radius, card padding/gap) so swapping tiers changes only
// the surface material, never the card's shape/spacing — but omits the panel-back depth layer,
// backdrop-blur/backdrop-saturate, box-shadow, and ring that make GlassCard "glass".
function QuietCard({ className, children, ...props }: React.ComponentProps<"div">) {
  return (
    <Card
      data-slot="quiet-card"
      className={cn(
        "gap-[var(--spacing-card-gap)] rounded-glass-md border bg-surface-quiet border-surface-quiet-border p-[var(--spacing-card-padding)] ring-0",
        className
      )}
      {...props}
    >
      {children}
    </Card>
  )
}

export { QuietCard }
