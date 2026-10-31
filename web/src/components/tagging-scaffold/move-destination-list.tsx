import { Button } from '@/components/ui/button'
import { ArchivedBadge } from './archived-badge'

// Shared by the Power Point (Room destinations) and Device (Power Point destinations) move
// dialogs. `items` is the caller's own non-archived candidate list, which may or may not still
// contain `currentId` — a Room/Power Point can be archived after something was created under it
// (ArchiveRoom/ArchivePowerPoint don't cascade-archive their children), so the current parent is
// not guaranteed to survive the archived-filter. The "any other option" check must therefore look
// for a non-current entry rather than comparing the list length against a fixed threshold.
export function MoveDestinationList<T extends { id: string }>({
  items,
  currentId,
  getLabel,
  onSelect,
  submitting,
  currentLabel,
  noDestinationsLabel,
}: {
  items: T[]
  currentId: string
  getLabel: (item: T) => string
  onSelect: (id: string) => void
  submitting: boolean
  currentLabel: string
  noDestinationsLabel: string
}) {
  const hasOtherDestination = items.some((item) => item.id !== currentId)
  if (!hasOtherDestination) {
    return <p className="text-muted-foreground text-sm">{noDestinationsLabel}</p>
  }

  return (
    <>
      {items.map((item) => {
        const isCurrent = item.id === currentId
        return (
          <Button
            key={item.id}
            type="button"
            variant={isCurrent ? 'outline' : 'ghost'}
            disabled={isCurrent || submitting}
            className="justify-between"
            onClick={() => onSelect(item.id)}
          >
            <span>{getLabel(item)}</span>
            {isCurrent && <ArchivedBadge label={currentLabel} />}
          </Button>
        )
      })}
    </>
  )
}
