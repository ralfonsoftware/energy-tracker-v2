import { useTranslation } from 'react-i18next'
import type { FormEvent } from 'react'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
} from '@/components/ui/dialog'
import { GLASS_MODAL_CLASSNAME } from '@/lib/glass-classnames'
import { MoveDestinationList } from './move-destination-list'
import type { DialogState, PowerPointDto, RoomDto } from './tagging-scaffold-types'

export function TaggingScaffoldDialogs({
  dialog,
  rooms,
  powerPoints,
  nameInput,
  onNameInputChange,
  submitting,
  dialogError,
  onClose,
  onSubmit,
  onDelete,
  onMoveTo,
}: {
  dialog: DialogState | null
  rooms: RoomDto[]
  powerPoints: PowerPointDto[]
  nameInput: string
  onNameInputChange: (value: string) => void
  submitting: boolean
  dialogError: string | null
  onClose: () => void
  onSubmit: (event: FormEvent<HTMLFormElement>) => void
  onDelete: () => void
  onMoveTo: (destinationId: string) => void
}) {
  const { t } = useTranslation()
  const roomsById = new Map(rooms.map((room) => [room.id, room]))

  return (
    <Dialog open={dialog !== null} onOpenChange={(open) => !open && !submitting && onClose()}>
      <DialogContent className={GLASS_MODAL_CLASSNAME}>
        {dialog?.kind === 'delete-room' && (
          <>
            <DialogHeader>
              <DialogTitle>{t('taggingScaffold.archive')}</DialogTitle>
              <DialogDescription>{t('taggingScaffold.confirmDeleteRoom')}</DialogDescription>
            </DialogHeader>
            {dialogError && <p className="text-destructive text-sm">{dialogError}</p>}
            <DialogFooter>
              <Button variant="outline" onClick={onClose} disabled={submitting}>
                {t('taggingScaffold.cancel')}
              </Button>
              <Button variant="glass-confirm" onClick={onDelete} disabled={submitting}>
                {t('taggingScaffold.archive')}
              </Button>
            </DialogFooter>
          </>
        )}

        {dialog?.kind === 'delete-power-point' && (
          <>
            <DialogHeader>
              <DialogTitle>{t('taggingScaffold.archive')}</DialogTitle>
              <DialogDescription>{t('taggingScaffold.confirmDeletePowerPoint')}</DialogDescription>
            </DialogHeader>
            {dialogError && <p className="text-destructive text-sm">{dialogError}</p>}
            <DialogFooter>
              <Button variant="outline" onClick={onClose} disabled={submitting}>
                {t('taggingScaffold.cancel')}
              </Button>
              <Button variant="glass-confirm" onClick={onDelete} disabled={submitting}>
                {t('taggingScaffold.archive')}
              </Button>
            </DialogFooter>
          </>
        )}

        {dialog?.kind === 'delete-device' && (
          <>
            <DialogHeader>
              <DialogTitle>{t('taggingScaffold.archive')}</DialogTitle>
              <DialogDescription>{t('taggingScaffold.confirmDeleteDevice')}</DialogDescription>
            </DialogHeader>
            {dialogError && <p className="text-destructive text-sm">{dialogError}</p>}
            <DialogFooter>
              <Button variant="outline" onClick={onClose} disabled={submitting}>
                {t('taggingScaffold.cancel')}
              </Button>
              <Button variant="glass-confirm" onClick={onDelete} disabled={submitting}>
                {t('taggingScaffold.archive')}
              </Button>
            </DialogFooter>
          </>
        )}

        {(dialog?.kind === 'move-power-point' || dialog?.kind === 'move-device') && (
          <>
            <DialogHeader>
              <DialogTitle>{t('taggingScaffold.moveTo')}</DialogTitle>
              <DialogDescription>
                {dialog.kind === 'move-power-point'
                  ? t('taggingScaffold.moveDescriptionPowerPoint')
                  : t('taggingScaffold.moveDescriptionDevice')}
              </DialogDescription>
            </DialogHeader>

            <div className="flex flex-col gap-1">
              {dialog.kind === 'move-power-point' && (
                <MoveDestinationList
                  items={rooms.filter((room) => !room.archivedAt)}
                  currentId={dialog.powerPoint.roomId}
                  getLabel={(room) => room.name}
                  onSelect={onMoveTo}
                  submitting={submitting}
                  currentLabel={t('taggingScaffold.currentBadge')}
                  noDestinationsLabel={t('taggingScaffold.noDestinations')}
                />
              )}

              {dialog.kind === 'move-device' && (
                <MoveDestinationList
                  items={powerPoints.filter((powerPoint) => !powerPoint.archivedAt)}
                  currentId={dialog.device.powerPointId}
                  getLabel={(powerPoint) => `${roomsById.get(powerPoint.roomId)?.name ?? ''} → ${powerPoint.name}`}
                  onSelect={onMoveTo}
                  submitting={submitting}
                  currentLabel={t('taggingScaffold.currentBadge')}
                  noDestinationsLabel={t('taggingScaffold.noDestinations')}
                />
              )}
            </div>

            {dialogError && <p className="text-destructive text-sm">{dialogError}</p>}

            <DialogFooter>
              <Button variant="outline" onClick={onClose} disabled={submitting}>
                {t('taggingScaffold.cancel')}
              </Button>
            </DialogFooter>
          </>
        )}

        {dialog &&
          dialog.kind !== 'delete-room' &&
          dialog.kind !== 'delete-power-point' &&
          dialog.kind !== 'delete-device' &&
          dialog.kind !== 'move-power-point' &&
          dialog.kind !== 'move-device' && (
            <form className="flex flex-col gap-4" onSubmit={onSubmit}>
              <DialogHeader>
                <DialogTitle>
                  {dialog.kind === 'create-room' && t('taggingScaffold.addRoom')}
                  {dialog.kind === 'rename-room' && t('taggingScaffold.rename')}
                  {dialog.kind === 'create-power-point' && t('taggingScaffold.addPowerPoint')}
                  {dialog.kind === 'rename-power-point' && t('taggingScaffold.rename')}
                  {dialog.kind === 'create-device' && t('taggingScaffold.addDevice')}
                  {dialog.kind === 'rename-device' && t('taggingScaffold.rename')}
                </DialogTitle>
              </DialogHeader>

              <div className="flex flex-col gap-2">
                <Label htmlFor="tagging-scaffold-name">{t('taggingScaffold.namePlaceholder')}</Label>
                <Input
                  id="tagging-scaffold-name"
                  value={nameInput}
                  onChange={(event) => onNameInputChange(event.target.value)}
                  placeholder={t('taggingScaffold.namePlaceholder')}
                  required
                  autoFocus
                />
              </div>

              {dialogError && <p className="text-destructive text-sm">{dialogError}</p>}

              <DialogFooter>
                <Button type="button" variant="outline" onClick={onClose} disabled={submitting}>
                  {t('taggingScaffold.cancel')}
                </Button>
                <Button type="submit" variant="glass-primary" disabled={submitting || !nameInput.trim()}>
                  {submitting ? t('taggingScaffold.saving') : t('taggingScaffold.save')}
                </Button>
              </DialogFooter>
            </form>
          )}
      </DialogContent>
    </Dialog>
  )
}
