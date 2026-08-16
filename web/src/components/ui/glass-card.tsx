import * as React from "react"

import { cn } from "@/lib/utils"
import { Card } from "@/components/ui/card"

// The product's signature two-layer glass panel (DESIGN/elevation-depth.md): a rear panel
// (surface-panel-back) offset behind a translucent front card (surface-glass), for real z-depth
// through stacking rather than a flat drop-shadow. Values verbatim from
// mockups/direction-green-eco.html's Status card (colors.md: "Spine wins on conflict with any
// mock") — the {rounded.md} radius is the drill-down-card size, per key-settings.html's `.card`.
function GlassCard({ className, children, ...props }: React.ComponentProps<"div">) {
  return (
    <div data-slot="glass-card-stack" className="relative">
      <div
        aria-hidden="true"
        data-slot="glass-card-panel-back"
        className="absolute inset-[6px_-4px_-8px_6px] rounded-glass-md bg-surface-panel-back"
      />
      <Card
        data-slot="glass-card"
        className={cn(
          "relative gap-[var(--spacing-card-gap)] rounded-glass-md border border-[rgba(255,255,255,0.85)] bg-surface-glass p-[var(--spacing-card-padding)] shadow-[0_20px_40px_rgba(40,70,30,0.16)] ring-0 backdrop-blur-[20px] backdrop-saturate-[1.4]",
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
