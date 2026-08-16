import * as React from "react"

import { cn } from "@/lib/utils"
import { Input } from "@/components/ui/input"

// The unit-inside-field pattern (key-settings.html's `.baseline-input-wrap`,
// key-log-reading-flow.html's `.kwh-input-wrap`): the unit reads as part of the value, inside the
// same bordered box as the number, never a separate label beside it.
interface UnitInputProps extends React.ComponentProps<typeof Input> {
  unit: string
  wrapperClassName?: string
}

function UnitInput({ className, unit, wrapperClassName, ...props }: UnitInputProps) {
  return (
    <div
      data-slot="unit-input-wrap"
      className={cn(
        "flex items-baseline gap-1.5 rounded-glass-sm border border-[rgba(40,70,50,0.16)] bg-[rgba(255,255,255,0.7)] px-3.5 py-2.5",
        "dark:border-[rgba(210,235,220,0.18)] dark:bg-[rgba(8,16,12,0.4)]",
        "focus-within:border-ring focus-within:ring-3 focus-within:ring-ring/50",
        wrapperClassName
      )}
    >
      <Input
        {...props}
        className={cn(
          "h-auto border-0 bg-transparent p-0 font-bold tabular-nums shadow-none focus-visible:ring-0",
          className
        )}
      />
      <span
        data-slot="unit-input-unit"
        className="shrink-0 text-sm font-semibold text-[rgba(30,42,28,0.62)] dark:text-[rgba(234,245,238,0.6)]"
      >
        {unit}
      </span>
    </div>
  )
}

export { UnitInput }
