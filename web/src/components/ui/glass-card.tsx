import * as React from "react"

import { cn } from "@/lib/utils"
import { Card } from "@/components/ui/card"

// The product's signature two-layer glass panel (DESIGN/elevation-depth.md): a rear panel
// (surface-panel-back) offset behind a translucent front card (surface-glass), for real z-depth
// through stacking rather than a flat drop-shadow. Values verbatim from
// mockups/direction-green-eco.html — 'md' (default) is the {rounded.md}/18px drill-down-card size
// (the `.phone.inset` frame), per key-settings.html's `.card`; 'lg' is the {rounded.lg}/28px hero
// size (the primary, non-`.inset` frame) reserved for the product's single highest-visual-weight
// surface, the Status card (Story 2.5).
interface GlassCardProps extends React.ComponentProps<"div"> {
  size?: "md" | "lg"
}

const PANEL_BACK_INSET = {
  md: "inset-[6px_-4px_-8px_6px]",
  lg: "inset-[9px_-6px_-13px_9px]",
} as const

const FRONT_CARD_RADIUS = {
  md: "rounded-glass-md",
  lg: "rounded-glass-lg",
} as const

const FRONT_CARD_PADDING = {
  md: "p-[var(--spacing-card-padding)]",
  lg: "p-[27px_23px_25px]",
} as const

function GlassCard({ className, children, size = "md", ...props }: GlassCardProps) {
  return (
    <div data-slot="glass-card-stack" className="relative">
      <div
        aria-hidden="true"
        data-slot="glass-card-panel-back"
        className={cn(PANEL_BACK_INSET[size], FRONT_CARD_RADIUS[size], "absolute bg-surface-panel-back")}
      />
      <Card
        data-slot="glass-card"
        className={cn(
          "relative gap-[var(--spacing-card-gap)] border border-[rgba(255,255,255,0.85)] bg-surface-glass shadow-[0_20px_40px_rgba(40,70,30,0.16)] ring-0 backdrop-blur-[20px] backdrop-saturate-[1.4]",
          FRONT_CARD_RADIUS[size],
          FRONT_CARD_PADDING[size],
          "dark:border-[rgba(210,235,220,0.16)] dark:shadow-[0_30px_60px_rgba(5,15,10,0.55)] dark:backdrop-blur-[28px] dark:backdrop-saturate-[1.6]",
          className
        )}
        {...props}
      >
        {children}
      </Card>
    </div>
  )
}

export { GlassCard }
