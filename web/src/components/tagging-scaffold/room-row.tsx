import { ChevronRight, Pencil, Trash2 } from 'lucide-react'
import { useTranslation } from 'react-i18next'
import { Button } from '@/components/ui/button'
import { ArchivedBadge } from './archived-badge'
import { PowerPointRow } from './power-point-row'
import { ROOM_ROW_BORDER_CLASS, WIDE_TILE_CLASS, WIDE_TILE_GRID_CLASS } from './tagging-scaffold-classes'
import type { DeviceDto, DialogState, PowerPointDto, RoomDto } from './tagging-scaffold-types'

export function RoomRow({
  room,
  powerPoints,
  devicesByPowerPoint,
  showArchived,
  powerPointHasVisibleChildren,
  onOpenDialog,
}: {
  room: RoomDto
  powerPoints: PowerPointDto[]
  devicesByPowerPoint: Map<string, DeviceDto[]>
  showArchived: boolean
  powerPointHasVisibleChildren: (powerPoint: PowerPointDto) => boolean
  onOpenDialog: (next: DialogState) => void
}) {
  const { t } = useTranslation()
  const archivedBadgeLabel = t('taggingScaffold.archivedBadge')
  const suppressRoomRow = !!room.archivedAt && !showArchived

  const visiblePowerPoints = powerPoints.filter(
    (powerPoint) => showArchived || !powerPoint.archivedAt || powerPointHasVisibleChildren(powerPoint),
  )

  const powerPointsList = (
    <div className={`flex flex-col pl-4 ${WIDE_TILE_GRID_CLASS} wide:px-3.5 wide:pb-2 wide:pl-3.5`}>
      {visiblePowerPoints.map((powerPoint) => (
        <PowerPointRow
          key={powerPoint.id}
          powerPoint={powerPoint}
          devices={devicesByPowerPoint.get(powerPoint.id) ?? []}
          showArchived={showArchived}
          onOpenDialog={onOpenDialog}
        />
      ))}
    </div>
  )

  if (suppressRoomRow) {
    return (
      <div
        className={`${ROOM_ROW_BORDER_CLASS} wide:col-span-full wide:border-b-0`}
      >
        {powerPointsList}
      </div>
    )
  }

  return (
    <details
      className={`group/room ${ROOM_ROW_BORDER_CLASS} ${WIDE_TILE_CLASS}`}
    >
      <summary className="flex cursor-pointer list-none items-center justify-between gap-2 px-3.5 py-3 text-sm font-semibold [&::-webkit-details-marker]:hidden">
        <span className="flex min-w-0 items-center gap-2 wide:flex-wrap break-words">
          <ChevronRight aria-hidden="true" className="size-3 shrink-0 transition-transform group-open/room:rotate-90 motion-reduce:transition-none" />
          <span>{room.name}</span>
          <span className="hidden wide:inline text-muted-foreground font-normal">
            — {t('taggingScaffold.powerPointCount', { count: visiblePowerPoints.length })}
          </span>
        </span>
        {room.archivedAt && <ArchivedBadge label={archivedBadgeLabel} />}
      </summary>

      <div className="flex flex-wrap items-center gap-1 px-3.5 pt-1 pb-3 pl-8">
        <Button
          variant="ghost"
          size="icon"
          aria-label={t('taggingScaffold.rename')}
          onClick={() => onOpenDialog({ kind: 'rename-room', room })}
        >
          <Pencil aria-hidden="true" />
        </Button>
        <Button
          variant="ghost"
          size="icon"
          aria-label={t('taggingScaffold.delete')}
          onClick={() => onOpenDialog({ kind: 'delete-room', room })}
        >
          <Trash2 aria-hidden="true" />
        </Button>
        {!room.archivedAt && (
          <Button size="sm" variant="glass-primary" className="wide:hidden" onClick={() => onOpenDialog({ kind: 'create-power-point', roomId: room.id })}>
            {t('taggingScaffold.addPowerPoint')}
          </Button>
        )}
      </div>

      {powerPointsList}

      {!room.archivedAt && (
        <button
          type="button"
          className="text-primary hidden wide:inline-flex px-3.5 pb-2 text-xs font-semibold"
          onClick={() => onOpenDialog({ kind: 'create-power-point', roomId: room.id })}
        >
          {t('taggingScaffold.addPowerPoint')}
        </button>
      )}
    </details>
  )
}
