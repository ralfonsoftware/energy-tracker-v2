import { Button } from '@/components/ui/button'
import { ArchivedBadge } from './archived-badge'

// Shared by the Power Point and Device move dialogs. `items` may not contain `currentId` (archiving does not cascade),
// so look for any non-current entry instead of comparing the list length.
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
