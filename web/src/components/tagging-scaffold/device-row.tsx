import { Move, Pencil, Trash2 } from 'lucide-react'
import { useTranslation } from 'react-i18next'
import { Button } from '@/components/ui/button'
import { ArchivedBadge } from './archived-badge'
import type { DeviceDto, DialogState } from './tagging-scaffold-types'

// Devices stay a plain single-column list at every width — never gridded (UX-DR29):
// reordering/moving across a 2D grid is a materially more ambiguous gesture.
export function DeviceList({
  devices,
  onOpenDialog,
}: {
  devices: DeviceDto[]
  onOpenDialog: (next: DialogState) => void
}) {
  const { t } = useTranslation()
  const archivedBadgeLabel = t('taggingScaffold.archivedBadge')

  return (
    <div className="flex flex-col gap-0.5 pb-2 pl-8">
      {devices.map((device) => (
        <div key={device.id} className="flex items-center justify-between gap-2 px-3.5 py-1 text-sm">
          <span className="flex items-center gap-2">
            <span aria-hidden="true" className="size-1 shrink-0 rounded-full bg-[rgba(30,42,28,0.3)] dark:bg-[rgba(234,245,238,0.3)]" />
            {device.name} {device.archivedAt && <ArchivedBadge label={archivedBadgeLabel} />}
          </span>
          <div className="flex items-center gap-1">
            <Button
              variant="ghost"
              size="icon"
              aria-label={t('taggingScaffold.rename')}
              onClick={() => onOpenDialog({ kind: 'rename-device', device })}
            >
              <Pencil aria-hidden="true" />
            </Button>
            <Button
              variant="ghost"
              size="icon"
              aria-label={t('taggingScaffold.delete')}
              onClick={() => onOpenDialog({ kind: 'delete-device', device })}
            >
              <Trash2 aria-hidden="true" />
            </Button>
            <Button
              variant="ghost"
              size="icon"
              aria-label={t('taggingScaffold.moveTo')}
              onClick={() => onOpenDialog({ kind: 'move-device', device })}
            >
              <Move aria-hidden="true" />
            </Button>
          </div>
        </div>
      ))}
    </div>
  )
}
