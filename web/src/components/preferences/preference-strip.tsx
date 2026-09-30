import { useRef, type KeyboardEvent, type ReactNode } from 'react'
import { Loader2 } from 'lucide-react'
import { cn } from '@/lib/utils'

export interface PreferenceStripOption<T extends string> {
  value: T
  label: string
  icon?: ReactNode
  // Short text glyph (e.g. a locale code) for options without an icon.
  code?: string
}

interface PreferenceStripProps<T extends string> {
  options: PreferenceStripOption<T>[]
  value: T
  onChange: (value: T) => void
  ariaLabelledBy?: string
  ariaLabel?: string
  disabled?: boolean
  // Option currently being saved (shows an inline spinner on that segment, dims the strip).
  pendingValue?: T
  'data-testid'?: string
}

// Story 8.10 (UX-DR32): generic, controlled icon strip with radiogroup semantics. Arrow keys move
// AND select (wrapping), Home/End jump to the ends. Knows nothing about themes or locales — Stories
// 8.11/8.12 reuse it. Handled keys stop propagation so a parent Radix menu's own key handling
// (focus-first/last item, typeahead — printable keys are swallowed too) never steals them.
export function PreferenceStrip<T extends string>({
  options,
  value,
  onChange,
  ariaLabelledBy,
  ariaLabel,
  disabled = false,
  pendingValue,
  'data-testid': testId,
}: PreferenceStripProps<T>) {
  const refs = useRef<Array<HTMLButtonElement | null>>([])
  const pending = pendingValue !== undefined
  // No checked segment (stored value outside the option list): keep the strip reachable.
  const hasChecked = options.some((o) => o.value === value)

  const select = (index: number) => {
    const next = options[index]
    if (!next) return
    // A save is in flight: input is ignored so a second onChange can't race the first.
    if (pending) return
    refs.current[index]?.focus()
    if (next.value !== value) {
      onChange(next.value)
    }
  }

  const handleKeyDown = (event: KeyboardEvent<HTMLDivElement>) => {
    if (disabled) return
    // Leave browser/OS shortcuts (Alt+Arrow = back, Ctrl/Cmd+Home/End, ...) alone.
    if (event.altKey || event.ctrlKey || event.metaKey) return
    // Index from the focused segment (value can be ahead of / behind focus during an optimistic
    // save or after a revert), falling back to the checked one.
    const focusedIndex = refs.current.findIndex((el) => el !== null && el === document.activeElement)
    const currentIndex = Math.max(
      0,
      focusedIndex !== -1 ? focusedIndex : options.findIndex((o) => o.value === value),
    )
    let target: number | null = null
    switch (event.key) {
      case 'ArrowRight':
      case 'ArrowDown':
        target = (currentIndex + 1) % options.length
        break
      case 'ArrowLeft':
      case 'ArrowUp':
        target = (currentIndex - 1 + options.length) % options.length
        break
      case 'Home':
        target = 0
        break
      case 'End':
        target = options.length - 1
        break
    }
    if (target === null) {
      // Printable characters would reach the parent Radix menu's typeahead and pull focus onto an item.
      if (event.key.length === 1) event.stopPropagation()
      return
    }
    event.preventDefault()
    event.stopPropagation()
    select(target)
  }

  return (
    <div
      role="radiogroup"
      aria-label={ariaLabel}
      aria-labelledby={ariaLabelledBy}
      aria-disabled={disabled || undefined}
      aria-busy={pending || undefined}
      data-testid={testId}
      onKeyDown={handleKeyDown}
      className={cn(
        'inline-flex shrink-0 gap-0.5 rounded-xl border border-[rgba(40,70,50,0.14)] bg-[rgba(255,255,255,0.6)] p-0.5',
        'dark:border-[rgba(210,235,220,0.18)] dark:bg-[rgba(220,245,230,0.07)]',
        disabled && 'opacity-45',
        pending && !disabled && 'opacity-65',
      )}
    >
      {options.map((option, index) => {
        const checked = option.value === value
        return (
          <button
            key={option.value}
            ref={(el) => {
              refs.current[index] = el
            }}
            type="button"
            role="radio"
            aria-checked={checked}
            aria-label={option.label}
            title={option.label}
            tabIndex={checked || (!hasChecked && index === 0) ? 0 : -1}
            disabled={disabled}
            onClick={() => {
              if (!checked && !pending) onChange(option.value)
            }}
            className={cn(
              'relative flex h-10 w-11 items-center justify-center rounded-[10px] border border-transparent text-xs outline-none',
              option.code && !option.icon ? 'font-bold' : 'font-semibold',
              // Extends the 44x40 visual segment to a 44x44 hit area (inset is measured inside the 1px border, hence 3px).
              "before:absolute before:inset-x-0 before:-inset-y-[3px] before:content-['']",
              'focus-visible:outline-2 focus-visible:outline-offset-1 focus-visible:outline-ring',
              'disabled:cursor-not-allowed',
              checked
                ? 'bg-nav-chrome-active-bg text-nav-chrome-active-foreground border-[rgba(30,122,97,0.55)] dark:border-[rgba(111,209,177,0.55)]'
                : 'text-muted-foreground hover:bg-[rgba(40,70,50,0.07)] dark:hover:bg-[rgba(220,245,230,0.1)]',
            )}
          >
            {pendingValue === option.value ? (
              <Loader2 className="size-4 motion-safe:animate-spin" aria-hidden="true" data-testid="preference-strip-spinner" />
            ) : (
              (option.icon ?? option.code)
            )}
          </button>
        )
      })}
    </div>
  )
}
