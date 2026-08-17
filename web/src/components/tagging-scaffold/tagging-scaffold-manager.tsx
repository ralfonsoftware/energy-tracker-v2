import { useEffect, useRef, useState, type FormEvent } from 'react'
import { useTranslation } from 'react-i18next'
import { ChevronRight, Move, Pencil, Trash2 } from 'lucide-react'
import { Button } from '@/components/ui/button'
import { GlassCard } from '@/components/ui/glass-card'
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

interface RoomDto {
  id: string
  name: string
  archivedAt: string | null
}

interface PowerPointDto {
  id: string
  roomId: string
  name: string
  archivedAt: string | null
}

interface DeviceDto {
  id: string
  powerPointId: string
  name: string
  archivedAt: string | null
}

type DialogState =
  | { kind: 'create-room' }
  | { kind: 'rename-room'; room: RoomDto }
  | { kind: 'delete-room'; room: RoomDto }
  | { kind: 'create-power-point'; roomId: string }
  | { kind: 'rename-power-point'; powerPoint: PowerPointDto }
  | { kind: 'delete-power-point'; powerPoint: PowerPointDto }
  | { kind: 'move-power-point'; powerPoint: PowerPointDto }
  | { kind: 'create-device'; powerPointId: string }
  | { kind: 'rename-device'; device: DeviceDto }
  | { kind: 'delete-device'; device: DeviceDto }
  | { kind: 'move-device'; device: DeviceDto }

class ApiError extends Error {
  status: number
  detail: string | null

  constructor(status: number, detail: string | null) {
    super(`Request failed with status ${status}`)
    this.status = status
    this.detail = detail
  }
}

async function toApiError(response: Response): Promise<ApiError> {
  try {
    const body = (await response.json()) as { detail?: string }
    return new ApiError(response.status, body.detail ?? null)
  } catch {
    return new ApiError(response.status, null)
  }
}

function ArchivedBadge({ label }: { label: string }) {
  return (
    <span className="rounded-full bg-muted px-2 py-0.5 text-xs text-muted-foreground">
      {label}
    </span>
  )
}

// Shared by the Power Point (Room destinations) and Device (Power Point destinations) move
// dialogs. `items` is the caller's own non-archived candidate list, which may or may not still
// contain `currentId` — a Room/Power Point can be archived after something was created under it
// (ArchiveRoom/ArchivePowerPoint don't cascade-archive their children), so the current parent is
// not guaranteed to survive the archived-filter. The "any other option" check must therefore look
// for a non-current entry rather than comparing the list length against a fixed threshold.
function MoveDestinationList<T extends { id: string }>({
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

// Fetches and manages the whole Room → Power Point → Device tagging scaffold (AC #1-#4). Three
// parallel GETs, no combined endpoint — unneeded complexity at this data scale (a household's
// Room/PowerPoint/Device count is small, dozens not thousands).
export function TaggingScaffoldManager() {
  const { t } = useTranslation()
  const [rooms, setRooms] = useState<RoomDto[]>([])
  const [powerPoints, setPowerPoints] = useState<PowerPointDto[]>([])
  const [devices, setDevices] = useState<DeviceDto[]>([])
  const [loading, setLoading] = useState(true)
  const [loadError, setLoadError] = useState(false)

  const [dialog, setDialog] = useState<DialogState | null>(null)
  const [nameInput, setNameInput] = useState('')
  const [submitting, setSubmitting] = useState(false)
  const [dialogError, setDialogError] = useState<string | null>(null)

  // Tracks the live `dialog` value outside of React's closure/batching timing so a request that
  // resolves after the user has already closed and reopened a different dialog can tell it's
  // stale and skip clobbering that newer dialog's state.
  const dialogRef = useRef<DialogState | null>(null)
  dialogRef.current = dialog

  useEffect(() => {
    let cancelled = false

    Promise.all([
      fetch('/api/rooms', { credentials: 'include' }),
      fetch('/api/power-points', { credentials: 'include' }),
      fetch('/api/devices', { credentials: 'include' }),
    ])
      .then(async ([roomsResponse, powerPointsResponse, devicesResponse]) => {
        if (!roomsResponse.ok || !powerPointsResponse.ok || !devicesResponse.ok) {
          throw new Error('Unexpected tagging scaffold response')
        }

        const [roomsData, powerPointsData, devicesData] = await Promise.all([
          roomsResponse.json() as Promise<RoomDto[]>,
          powerPointsResponse.json() as Promise<PowerPointDto[]>,
          devicesResponse.json() as Promise<DeviceDto[]>,
        ])

        if (!cancelled) {
          setRooms(roomsData)
          setPowerPoints(powerPointsData)
          setDevices(devicesData)
        }
      })
      .catch(() => {
        if (!cancelled) {
          setLoadError(true)
        }
      })
      .finally(() => {
        if (!cancelled) {
          setLoading(false)
        }
      })

    return () => {
      cancelled = true
    }
  }, [])

  const roomsById = new Map(rooms.map((room) => [room.id, room]))

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

  const openDialog = (next: DialogState) => {
    setDialogError(null)
    if (next.kind === 'rename-room') {
      setNameInput(next.room.name)
    } else if (next.kind === 'rename-power-point') {
      setNameInput(next.powerPoint.name)
    } else if (next.kind === 'rename-device') {
      setNameInput(next.device.name)
    } else {
      setNameInput('')
    }
    setDialog(next)
  }

  const closeDialog = () => {
    setDialog(null)
    setDialogError(null)
    setNameInput('')
  }

  // Only closes if `target` (the dialog open when the just-finished request started) is still
  // the dialog that's open now — a stale, late-resolving request must not reset a dialog the
  // user has since closed and reopened for a different Room/Power Point/Device.
  const closeDialogIfUnchanged = (target: DialogState) => {
    if (dialogRef.current === target) {
      closeDialog()
    }
  }

  const handleSubmit = async (event: FormEvent<HTMLFormElement>) => {
    event.preventDefault()
    if (
      !dialog ||
      dialog.kind === 'delete-room' ||
      dialog.kind === 'delete-power-point' ||
      dialog.kind === 'delete-device' ||
      dialog.kind === 'move-power-point' ||
      dialog.kind === 'move-device'
    ) {
      return
    }

    const target = dialog
    setSubmitting(true)
    setDialogError(null)

    try {
      switch (dialog.kind) {
        case 'create-room': {
          const response = await fetch('/api/rooms', {
            method: 'POST',
            credentials: 'include',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ name: nameInput }),
          })
          if (!response.ok) {
            throw await toApiError(response)
          }
          const room = (await response.json()) as RoomDto
          setRooms((current) => [...current, room])
          break
        }
        case 'rename-room': {
          const response = await fetch(`/api/rooms/${dialog.room.id}`, {
            method: 'PUT',
            credentials: 'include',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ name: nameInput }),
          })
          if (!response.ok) {
            throw await toApiError(response)
          }
          const room = (await response.json()) as RoomDto
          setRooms((current) => current.map((r) => (r.id === room.id ? room : r)))
          break
        }
        case 'create-power-point': {
          const response = await fetch('/api/power-points', {
            method: 'POST',
            credentials: 'include',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ roomId: dialog.roomId, name: nameInput }),
          })
          if (!response.ok) {
            throw await toApiError(response)
          }
          const powerPoint = (await response.json()) as PowerPointDto
          setPowerPoints((current) => [...current, powerPoint])
          break
        }
        case 'rename-power-point': {
          const response = await fetch(`/api/power-points/${dialog.powerPoint.id}`, {
            method: 'PUT',
            credentials: 'include',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ name: nameInput }),
          })
          if (!response.ok) {
            throw await toApiError(response)
          }
          const powerPoint = (await response.json()) as PowerPointDto
          setPowerPoints((current) => current.map((p) => (p.id === powerPoint.id ? powerPoint : p)))
          break
        }
        case 'create-device': {
          const response = await fetch('/api/devices', {
            method: 'POST',
            credentials: 'include',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ powerPointId: dialog.powerPointId, name: nameInput }),
          })
          if (!response.ok) {
            throw await toApiError(response)
          }
          const device = (await response.json()) as DeviceDto
          setDevices((current) => [...current, device])
          break
        }
        case 'rename-device': {
          const response = await fetch(`/api/devices/${dialog.device.id}`, {
            method: 'PUT',
            credentials: 'include',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ name: nameInput }),
          })
          if (!response.ok) {
            throw await toApiError(response)
          }
          const device = (await response.json()) as DeviceDto
          setDevices((current) => current.map((d) => (d.id === device.id ? device : d)))
          break
        }
      }

      closeDialogIfUnchanged(target)
    } catch (err) {
      // A race — another tab archived the parent while this dialog was open — is the one
      // scenario worth a distinct message; the client-side picker exclusion below only prevents
      // *opening* a create dialog for an already-archived parent visible in this tab's data.
      // Otherwise, prefer the backend's own validation detail (e.g. a too-long or duplicate
      // Name) over a generic message, when the server sent one.
      if (err instanceof ApiError && err.status === 409) {
        setDialogError(t('taggingScaffold.errorParentArchived'))
      } else if (err instanceof ApiError && err.detail) {
        setDialogError(err.detail)
      } else {
        setDialogError(t('taggingScaffold.errorGeneric'))
      }
    } finally {
      setSubmitting(false)
    }
  }

  const handleDelete = async () => {
    if (!dialog) {
      return
    }

    const target = dialog
    setSubmitting(true)
    setDialogError(null)

    try {
      if (dialog.kind === 'delete-room') {
        const response = await fetch(`/api/rooms/${dialog.room.id}`, { method: 'DELETE', credentials: 'include' })
        if (!response.ok) {
          throw await toApiError(response)
        }
        const room = (await response.json()) as RoomDto
        setRooms((current) => current.map((r) => (r.id === room.id ? room : r)))
      } else if (dialog.kind === 'delete-power-point') {
        const response = await fetch(`/api/power-points/${dialog.powerPoint.id}`, { method: 'DELETE', credentials: 'include' })
        if (!response.ok) {
          throw await toApiError(response)
        }
        const powerPoint = (await response.json()) as PowerPointDto
        setPowerPoints((current) => current.map((p) => (p.id === powerPoint.id ? powerPoint : p)))
      } else if (dialog.kind === 'delete-device') {
        const response = await fetch(`/api/devices/${dialog.device.id}`, { method: 'DELETE', credentials: 'include' })
        if (!response.ok) {
          throw await toApiError(response)
        }
        const device = (await response.json()) as DeviceDto
        setDevices((current) => current.map((d) => (d.id === device.id ? device : d)))
      } else {
        return
      }

      closeDialogIfUnchanged(target)
    } catch (err) {
      setDialogError(err instanceof ApiError && err.detail ? err.detail : t('taggingScaffold.errorGeneric'))
    } finally {
      setSubmitting(false)
    }
  }

  const handleMoveTo = async (destinationId: string) => {
    if (!dialog || (dialog.kind !== 'move-power-point' && dialog.kind !== 'move-device')) {
      return
    }

    const target = dialog
    setSubmitting(true)
    setDialogError(null)

    try {
      if (dialog.kind === 'move-power-point') {
        const response = await fetch(`/api/power-points/${dialog.powerPoint.id}/room`, {
          method: 'PUT',
          credentials: 'include',
          headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify({ roomId: destinationId }),
        })
        if (!response.ok) {
          throw await toApiError(response)
        }
        const powerPoint = (await response.json()) as PowerPointDto
        setPowerPoints((current) => current.map((p) => (p.id === powerPoint.id ? powerPoint : p)))
      } else {
        const response = await fetch(`/api/devices/${dialog.device.id}/power-point`, {
          method: 'PUT',
          credentials: 'include',
          headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify({ powerPointId: destinationId }),
        })
        if (!response.ok) {
          throw await toApiError(response)
        }
        const device = (await response.json()) as DeviceDto
        setDevices((current) => current.map((d) => (d.id === device.id ? device : d)))
      }

      closeDialogIfUnchanged(target)
    } catch (err) {
      if (err instanceof ApiError && err.status === 409) {
        setDialogError(t('taggingScaffold.errorParentArchived'))
      } else if (err instanceof ApiError && err.detail) {
        setDialogError(err.detail)
      } else {
        setDialogError(t('taggingScaffold.errorGeneric'))
      }
    } finally {
      setSubmitting(false)
    }
  }

  const archivedBadgeLabel = t('taggingScaffold.archivedBadge')

  return (
    <div className="flex flex-col gap-4">
      <div className="flex items-center justify-between">
        <h2 className="text-lg font-semibold">{t('taggingScaffold.heading')}</h2>
        <Button onClick={() => openDialog({ kind: 'create-room' })}>{t('taggingScaffold.addRoom')}</Button>
      </div>

      {loading && <p className="text-muted-foreground text-sm">{t('taggingScaffold.loading')}</p>}
      {loadError && <p className="text-destructive text-sm">{t('taggingScaffold.errorGeneric')}</p>}
      {!loading && !loadError && rooms.length === 0 && (
        <p className="text-muted-foreground text-sm">{t('taggingScaffold.roomsEmpty')}</p>
      )}

      {rooms.length > 0 && (
        <GlassCard className="gap-0 p-0">
          {rooms.map((room) => (
            <details
              key={room.id}
              className="group/room border-b border-[rgba(40,70,50,0.09)] last:border-b-0 dark:border-[rgba(210,235,220,0.1)]"
            >
              <summary className="flex cursor-pointer list-none items-center justify-between gap-2 px-3.5 py-3 text-sm font-semibold [&::-webkit-details-marker]:hidden">
                <span className="flex items-center gap-2">
                  <ChevronRight aria-hidden="true" className="size-3 shrink-0 transition-transform group-open/room:rotate-90 motion-reduce:transition-none" />
                  <span>{room.name}</span>
                </span>
                {room.archivedAt && <ArchivedBadge label={archivedBadgeLabel} />}
              </summary>

              <div className="flex flex-wrap items-center gap-1 px-3.5 pt-1 pb-3 pl-8">
                <Button
                  variant="ghost"
                  size="icon"
                  aria-label={t('taggingScaffold.rename')}
                  onClick={() => openDialog({ kind: 'rename-room', room })}
                >
                  <Pencil aria-hidden="true" />
                </Button>
                <Button
                  variant="ghost"
                  size="icon"
                  aria-label={t('taggingScaffold.delete')}
                  onClick={() => openDialog({ kind: 'delete-room', room })}
                >
                  <Trash2 aria-hidden="true" />
                </Button>
                {!room.archivedAt && (
                  <Button size="sm" variant="glass-primary" onClick={() => openDialog({ kind: 'create-power-point', roomId: room.id })}>
                    {t('taggingScaffold.addPowerPoint')}
                  </Button>
                )}
              </div>

              <div className="flex flex-col pl-4">
                {(powerPointsByRoom.get(room.id) ?? []).map((powerPoint) => (
                  <details
                    key={powerPoint.id}
                    className="group/pp border-t border-[rgba(40,70,50,0.08)] dark:border-[rgba(210,235,220,0.08)]"
                  >
                    <summary className="flex cursor-pointer list-none items-center justify-between gap-2 px-3.5 py-2.5 text-sm font-semibold [&::-webkit-details-marker]:hidden">
                      <span className="flex items-center gap-2">
                        <ChevronRight aria-hidden="true" className="size-3 shrink-0 transition-transform group-open/pp:rotate-90 motion-reduce:transition-none" />
                        <span>{powerPoint.name}</span>
                      </span>
                      {powerPoint.archivedAt && <ArchivedBadge label={archivedBadgeLabel} />}
                    </summary>

                    <div className="flex flex-wrap items-center gap-1 px-3.5 pt-1 pb-2.5 pl-8">
                      <Button
                        variant="ghost"
                        size="icon"
                        aria-label={t('taggingScaffold.rename')}
                        onClick={() => openDialog({ kind: 'rename-power-point', powerPoint })}
                      >
                        <Pencil aria-hidden="true" />
                      </Button>
                      <Button
                        variant="ghost"
                        size="icon"
                        aria-label={t('taggingScaffold.delete')}
                        onClick={() => openDialog({ kind: 'delete-power-point', powerPoint })}
                      >
                        <Trash2 aria-hidden="true" />
                      </Button>
                      <Button
                        variant="ghost"
                        size="icon"
                        aria-label={t('taggingScaffold.moveTo')}
                        onClick={() => openDialog({ kind: 'move-power-point', powerPoint })}
                      >
                        <Move aria-hidden="true" />
                      </Button>
                      {!powerPoint.archivedAt && (
                        <Button size="sm" variant="glass-primary" onClick={() => openDialog({ kind: 'create-device', powerPointId: powerPoint.id })}>
                          {t('taggingScaffold.addDevice')}
                        </Button>
                      )}
                    </div>

                    <div className="flex flex-col gap-0.5 pb-2 pl-8">
                      {(devicesByPowerPoint.get(powerPoint.id) ?? []).map((device) => (
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
                              onClick={() => openDialog({ kind: 'rename-device', device })}
                            >
                              <Pencil aria-hidden="true" />
                            </Button>
                            <Button
                              variant="ghost"
                              size="icon"
                              aria-label={t('taggingScaffold.delete')}
                              onClick={() => openDialog({ kind: 'delete-device', device })}
                            >
                              <Trash2 aria-hidden="true" />
                            </Button>
                            <Button
                              variant="ghost"
                              size="icon"
                              aria-label={t('taggingScaffold.moveTo')}
                              onClick={() => openDialog({ kind: 'move-device', device })}
                            >
                              <Move aria-hidden="true" />
                            </Button>
                          </div>
                        </div>
                      ))}
                    </div>
                  </details>
                ))}
              </div>
            </details>
          ))}
        </GlassCard>
      )}

      <Dialog open={dialog !== null} onOpenChange={(open) => !open && !submitting && closeDialog()}>
        <DialogContent className={GLASS_MODAL_CLASSNAME}>
          {dialog?.kind === 'delete-room' && (
            <>
              <DialogHeader>
                <DialogTitle>{t('taggingScaffold.archive')}</DialogTitle>
                <DialogDescription>{t('taggingScaffold.confirmDeleteRoom')}</DialogDescription>
              </DialogHeader>
              {dialogError && <p className="text-destructive text-sm">{dialogError}</p>}
              <DialogFooter>
                <Button variant="outline" onClick={closeDialog} disabled={submitting}>
                  {t('taggingScaffold.cancel')}
                </Button>
                <Button variant="glass-confirm" onClick={handleDelete} disabled={submitting}>
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
                <Button variant="outline" onClick={closeDialog} disabled={submitting}>
                  {t('taggingScaffold.cancel')}
                </Button>
                <Button variant="glass-confirm" onClick={handleDelete} disabled={submitting}>
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
                <Button variant="outline" onClick={closeDialog} disabled={submitting}>
                  {t('taggingScaffold.cancel')}
                </Button>
                <Button variant="glass-confirm" onClick={handleDelete} disabled={submitting}>
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
                    onSelect={handleMoveTo}
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
                    onSelect={handleMoveTo}
                    submitting={submitting}
                    currentLabel={t('taggingScaffold.currentBadge')}
                    noDestinationsLabel={t('taggingScaffold.noDestinations')}
                  />
                )}
              </div>

              {dialogError && <p className="text-destructive text-sm">{dialogError}</p>}

              <DialogFooter>
                <Button variant="outline" onClick={closeDialog} disabled={submitting}>
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
              <form className="flex flex-col gap-4" onSubmit={handleSubmit}>
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
                    onChange={(event) => setNameInput(event.target.value)}
                    placeholder={t('taggingScaffold.namePlaceholder')}
                    required
                    autoFocus
                  />
                </div>

                {dialogError && <p className="text-destructive text-sm">{dialogError}</p>}

                <DialogFooter>
                  <Button type="button" variant="outline" onClick={closeDialog} disabled={submitting}>
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
    </div>
  )
}
