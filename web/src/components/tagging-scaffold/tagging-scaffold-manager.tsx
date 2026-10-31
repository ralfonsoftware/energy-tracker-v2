import { useState } from 'react'
import { useTranslation } from 'react-i18next'
import { Eye, EyeOff } from 'lucide-react'
import { Button } from '@/components/ui/button'
import { GlassCard } from '@/components/ui/glass-card'
import { RoomRow } from './room-row'
import { TaggingScaffoldDialogs } from './tagging-scaffold-dialogs'
import { WIDE_TILE_GRID_CLASS } from './tagging-scaffold-classes'
import type { DeviceDto, PowerPointDto, RoomDto } from './tagging-scaffold-types'
import { useTaggingScaffoldData } from './use-tagging-scaffold-data'
import { useTaggingScaffoldDialog } from './use-tagging-scaffold-dialog'

export function TaggingScaffoldManager() {
  const { t } = useTranslation()
  const { rooms, setRooms, powerPoints, setPowerPoints, devices, setDevices, loading, loadError } =
    useTaggingScaffoldData()
  const {
    dialog,
    nameInput,
    setNameInput,
    submitting,
    dialogError,
    openDialog,
    closeDialog,
    handleSubmit,
    handleDelete,
    handleMoveTo,
  } = useTaggingScaffoldDialog({ setRooms, setPowerPoints, setDevices })
  // No persistence by design (AC #6) — resets to hidden on every mount.
  const [showArchived, setShowArchived] = useState(false)

  const powerPointsByRoom = new Map<string, PowerPointDto[]>()
  for (const powerPoint of powerPoints) {
    const siblings = powerPointsByRoom.get(powerPoint.roomId) ?? []
    siblings.push(powerPoint)
    powerPointsByRoom.set(powerPoint.roomId, siblings)
  }

  const devicesByPowerPoint = new Map<string, DeviceDto[]>()
  for (const device of devices) {
    const siblings = devicesByPowerPoint.get(device.powerPointId) ?? []
    siblings.push(device)
    devicesByPowerPoint.set(device.powerPointId, siblings)
  }

  // AC #5: an archived parent with live children must not hide them. A Room/Power Point is absent only when archived, hidden and
  // without visible children; roomHasVisibleChildren recurses through powerPointHasVisibleChildren for that.
  const powerPointHasVisibleChildren = (powerPoint: PowerPointDto) =>
    (devicesByPowerPoint.get(powerPoint.id) ?? []).some((device) => showArchived || !device.archivedAt)
  const roomHasVisibleChildren = (room: RoomDto) =>
    (powerPointsByRoom.get(room.id) ?? []).some(
      (pp) => showArchived || !pp.archivedAt || powerPointHasVisibleChildren(pp),
    )

  return (
    <div className="flex flex-col gap-4">
    <div className="flex items-center justify-between">
      <h2 className="wide:hidden text-lg font-semibold">{t('taggingScaffold.heading')}</h2>
      <div className="flex items-center gap-1">
        <Button
          variant="ghost"
          size="icon"
          aria-pressed={showArchived}
          aria-label={
            showArchived ? t('taggingScaffold.hideArchivedToggle') : t('taggingScaffold.showArchivedToggle')
          }
          onClick={() => setShowArchived((current) => !current)}
        >
          {showArchived ? <Eye aria-hidden="true" /> : <EyeOff aria-hidden="true" />}
        </Button>
        <Button onClick={() => openDialog({ kind: 'create-room' })}>{t('taggingScaffold.addRoom')}</Button>
      </div>
    </div>

    {loading && <p className="text-muted-foreground text-sm">{t('taggingScaffold.loading')}</p>}
    {loadError && <p className="text-destructive text-sm">{t('taggingScaffold.errorGeneric')}</p>}
    {!loading && !loadError && rooms.length === 0 && (
      <p className="text-muted-foreground text-sm">{t('taggingScaffold.roomsEmpty')}</p>
    )}

      {rooms.length > 0 && (
        <GlassCard className="gap-0 p-0">
          <div className={`flex flex-col ${WIDE_TILE_GRID_CLASS} wide:p-3`}>
            {rooms
              .filter((room) => showArchived || !room.archivedAt || roomHasVisibleChildren(room))
              .map((room) => (
                <RoomRow
                  key={room.id}
                  room={room}
                  powerPoints={powerPointsByRoom.get(room.id) ?? []}
                  devicesByPowerPoint={devicesByPowerPoint}
                  showArchived={showArchived}
                  powerPointHasVisibleChildren={powerPointHasVisibleChildren}
                  onOpenDialog={openDialog}
                />
              ))}
          </div>
        </GlassCard>
      )}

      <TaggingScaffoldDialogs
        dialog={dialog}
        rooms={rooms}
        powerPoints={powerPoints}
        nameInput={nameInput}
        onNameInputChange={setNameInput}
        submitting={submitting}
        dialogError={dialogError}
        onClose={closeDialog}
        onSubmit={handleSubmit}
        onDelete={handleDelete}
        onMoveTo={handleMoveTo}
      />
    </div>
  )
}
