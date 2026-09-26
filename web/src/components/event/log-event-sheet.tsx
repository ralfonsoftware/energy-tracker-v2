import { useEffect, useState, type FormEvent, type ReactNode } from 'react'
import { useTranslation } from 'react-i18next'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import {
  Sheet,
  SheetContent,
  SheetDescription,
  SheetHeader,
  SheetTitle,
  SheetTrigger,
} from '@/components/ui/sheet'
import { GLASS_SHEET_CLASSNAME } from '@/lib/glass-classnames'
import { createEvent, fetchTagOptions, messageForEventError, type EventDto, type TagOption } from '@/lib/event-api'

const MAX_DESCRIPTION_LENGTH = 500

// datetime-local's value format is local time with no offset (YYYY-MM-DDTHH:mm) — same helper as
// log-reading-sheet.tsx.
function toDateTimeLocalValue(date: Date): string {
  const pad = (n: number) => String(n).padStart(2, '0')
  return `${date.getFullYear()}-${pad(date.getMonth() + 1)}-${pad(date.getDate())}T${pad(date.getHours())}:${pad(date.getMinutes())}`
}

type TagLoadState = 'loading' | 'loaded' | 'failed'

interface LogEventSheetProps {
  trigger: ReactNode
  open: boolean
  onOpenChange: (open: boolean) => void
  // The saved Event is handed back so the host can render its own confirmation where the layout
  // allows one — this component deliberately renders none, because its trigger lives in the
  // Dashboard's header row (a fixed-height icon-only square below 660px, an auto-width labeled
  // pill at ≥660px — either way too cramped for an inline confirmation).
  onSaved?: (event: EventDto) => void
}

export function LogEventSheet({ trigger, open, onOpenChange, onSaved }: LogEventSheetProps) {
  const { t } = useTranslation()
  const [description, setDescription] = useState('')
  const [occurredAt, setOccurredAt] = useState(() => toDateTimeLocalValue(new Date()))
  const [tagOptions, setTagOptions] = useState<TagOption[]>([])
  const [tagLoadState, setTagLoadState] = useState<TagLoadState>('loading')
  const [selectedTag, setSelectedTag] = useState<TagOption | null>(null)
  const [submitting, setSubmitting] = useState(false)
  const [error, setError] = useState<string | null>(null)

  // Fired fresh every time the sheet opens so a Room/Power Point/Device created or archived
  // elsewhere since the last open is reflected.
  useEffect(() => {
    if (!open) {
      return
    }

    let cancelled = false
    setTagLoadState('loading')

    fetchTagOptions()
      .then((options) => {
        if (!cancelled) {
          setTagOptions(options)
          setTagLoadState('loaded')
        }
      })
      .catch(() => {
        // Surfaced as a distinct failed state rather than an empty list — an empty picker and a
        // broken picker must not look identical to the user.
        if (!cancelled) {
          setTagOptions([])
          setTagLoadState('failed')
        }
      })

    return () => {
      cancelled = true
    }
  }, [open])

  const handleOpenChange = (next: boolean) => {
    if (submitting) {
      return
    }
    if (next) {
      setDescription('')
      setOccurredAt(toDateTimeLocalValue(new Date()))
      setSelectedTag(null)
      setError(null)
    }
    onOpenChange(next)
  }

  const handleSubmit = async (event: FormEvent<HTMLFormElement>) => {
    event.preventDefault()

    setSubmitting(true)
    setError(null)

    try {
      const saved = await createEvent({
        description,
        occurredAt: new Date(occurredAt).toISOString(),
        taggedEntityType: selectedTag?.type ?? null,
        taggedEntityId: selectedTag?.id ?? null,
      })

      onOpenChange(false)
      onSaved?.(saved)
    } catch (err) {
      setError(messageForEventError(err, t))
    } finally {
      setSubmitting(false)
    }
  }

  return (
    <Sheet open={open} onOpenChange={handleOpenChange}>
      <SheetTrigger asChild>{trigger}</SheetTrigger>
      <SheetContent side="bottom" className={GLASS_SHEET_CLASSNAME}>
        <form onSubmit={handleSubmit} className="flex flex-col gap-4 px-4 pb-4">
          <SheetHeader className="px-0">
            <SheetTitle>{t('event.sheetTitle')}</SheetTitle>
            <SheetDescription>{t('event.sheetDescription')}</SheetDescription>
          </SheetHeader>

          <div className="flex flex-col gap-2">
            <Label htmlFor="event-description">{t('event.descriptionLabel')}</Label>
            <Input
              id="event-description"
              type="text"
              value={description}
              onChange={(event) => setDescription(event.target.value)}
              placeholder={t('event.descriptionPlaceholder')}
              maxLength={MAX_DESCRIPTION_LENGTH}
              disabled={submitting}
              required
              autoFocus
            />
          </div>

          <div className="flex flex-col gap-2">
            <Label htmlFor="event-timestamp">{t('event.dateTimeLabel')}</Label>
            <Input
              id="event-timestamp"
              type="datetime-local"
              value={occurredAt}
              onChange={(event) => setOccurredAt(event.target.value)}
              disabled={submitting}
              required
            />
          </div>

          <div className="flex flex-col gap-2">
            <span id="event-tag-label" className="text-sm leading-none font-medium">
              {t('event.tagLabel')}
            </span>
            {tagLoadState === 'failed' ? (
              <p className="text-muted-foreground text-sm">{t('event.tagLoadFailed')}</p>
            ) : tagLoadState === 'loading' ? (
              <p className="text-muted-foreground text-sm">{t('event.tagLoading')}</p>
            ) : (
              // radiogroup, not a bare button list — selection is otherwise conveyed only by the
              // visual `variant`, which a screen reader cannot observe.
              <div
                role="radiogroup"
                aria-labelledby="event-tag-label"
                className="flex max-h-48 flex-col gap-1 overflow-y-auto"
              >
                <Button
                  type="button"
                  role="radio"
                  aria-checked={selectedTag === null}
                  variant={selectedTag === null ? 'outline' : 'ghost'}
                  className="justify-start"
                  disabled={submitting}
                  onClick={() => setSelectedTag(null)}
                >
                  {t('event.noTag')}
                </Button>
                {tagOptions.map((option) => {
                  const isSelected = selectedTag?.type === option.type && selectedTag?.id === option.id
                  return (
                    <Button
                      key={`${option.type}-${option.id}`}
                      type="button"
                      role="radio"
                      aria-checked={isSelected}
                      variant={isSelected ? 'outline' : 'ghost'}
                      className="justify-start"
                      disabled={submitting}
                      onClick={() => setSelectedTag(option)}
                    >
                      {option.label}
                    </Button>
                  )
                })}
              </div>
            )}
          </div>

          {error && (
            <p role="alert" className="text-destructive text-sm">
              {error}
            </p>
          )}

          <Button type="submit" variant="glass-primary" disabled={submitting || !description.trim()}>
            {submitting ? t('event.saving') : t('event.save')}
          </Button>
        </form>
      </SheetContent>
    </Sheet>
  )
}
