import { ChevronRight, Move, Pencil, Trash2 } from 'lucide-react'
import { useTranslation } from 'react-i18next'
import { Button } from '@/components/ui/button'
import { ArchivedBadge } from './archived-badge'
import { DeviceList } from './device-list'
import { WIDE_TILE_CLASS } from './tagging-scaffold-classes'
import type { DeviceDto, DialogState, PowerPointDto } from './tagging-scaffold-types'

export function PowerPointRow({
  powerPoint,
  devices,
  showArchived,
  onOpenDialog,
}: {
  powerPoint: PowerPointDto
  devices: DeviceDto[]
  showArchived: boolean
  onOpenDialog: (next: DialogState) => void
}) {
  const { t } = useTranslation()
  const archivedBadgeLabel = t('taggingScaffold.archivedBadge')
  const suppressPowerPointRow = !!powerPoint.archivedAt && !showArchived

  const visibleDevices = devices.filter((device) => showArchived || !device.archivedAt)

  const deviceList = <DeviceList devices={visibleDevices} onOpenDialog={onOpenDialog} />

  if (suppressPowerPointRow) {
    return (
      <div
        className="border-t border-[rgba(40,70,50,0.08)] dark:border-[rgba(210,235,220,0.08)] wide:col-span-full wide:border-t-0"
      >
        {deviceList}
      </div>
    )
  }

  return (
    <details
      className={`group/pp border-t border-[rgba(40,70,50,0.08)] dark:border-[rgba(210,235,220,0.08)] ${WIDE_TILE_CLASS}`}
    >
      <summary className="flex cursor-pointer list-none items-center justify-between gap-2 px-3.5 py-2.5 text-sm font-semibold [&::-webkit-details-marker]:hidden">
        <span className="flex min-w-0 items-center gap-2 wide:flex-wrap break-words">
          <ChevronRight aria-hidden="true" className="size-3 shrink-0 transition-transform group-open/pp:rotate-90 motion-reduce:transition-none" />
          <span>{powerPoint.name}</span>
          <span className="hidden wide:inline text-muted-foreground font-normal">
            — {t('taggingScaffold.deviceCount', { count: visibleDevices.length })}
          </span>
        </span>
        {powerPoint.archivedAt && <ArchivedBadge label={archivedBadgeLabel} />}
      </summary>

      <div className="flex flex-wrap items-center gap-1 px-3.5 pt-1 pb-2.5 pl-8">
        <Button
          variant="ghost"
          size="icon"
          aria-label={t('taggingScaffold.rename')}
          onClick={() => onOpenDialog({ kind: 'rename-power-point', powerPoint })}
        >
          <Pencil aria-hidden="true" />
        </Button>
        <Button
          variant="ghost"
          size="icon"
          aria-label={t('taggingScaffold.delete')}
          onClick={() => onOpenDialog({ kind: 'delete-power-point', powerPoint })}
        >
          <Trash2 aria-hidden="true" />
        </Button>
        <Button
          variant="ghost"
          size="icon"
          aria-label={t('taggingScaffold.moveTo')}
          onClick={() => onOpenDialog({ kind: 'move-power-point', powerPoint })}
        >
          <Move aria-hidden="true" />
        </Button>
        {!powerPoint.archivedAt && (
          <Button size="sm" variant="glass-primary" className="wide:hidden" onClick={() => onOpenDialog({ kind: 'create-device', powerPointId: powerPoint.id })}>
            {t('taggingScaffold.addDevice')}
          </Button>
        )}
      </div>

      {deviceList}

      {!powerPoint.archivedAt && (
        <button
          type="button"
          className="text-primary hidden wide:inline-flex px-3.5 pb-2 text-xs font-semibold"
          onClick={() => onOpenDialog({ kind: 'create-device', powerPointId: powerPoint.id })}
        >
          {t('taggingScaffold.addDevice')}
        </button>
      )}
    </details>
  )
}
