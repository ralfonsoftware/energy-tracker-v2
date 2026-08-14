import { useEffect, useRef, useState, type FormEvent } from 'react'
import { useTranslation } from 'react-i18next'
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
  | { kind: 'create-device'; powerPointId: string }
  | { kind: 'rename-device'; device: DeviceDto }
  | { kind: 'delete-device'; device: DeviceDto }

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
    if (!dialog || dialog.kind === 'delete-room' || dialog.kind === 'delete-power-point' || dialog.kind === 'delete-device') {
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

      <div className="flex flex-col gap-2">
        {rooms.map((room) => (
          <details key={room.id} className="rounded-lg border border-border p-3">
            <summary className="cursor-pointer font-medium">
              {room.name} {room.archivedAt && <ArchivedBadge label={archivedBadgeLabel} />}
            </summary>

            <div className="mt-2 flex flex-wrap gap-2">
              <Button size="sm" variant="outline" onClick={() => openDialog({ kind: 'rename-room', room })}>
                {t('taggingScaffold.rename')}
              </Button>
              <Button size="sm" variant="outline" onClick={() => openDialog({ kind: 'delete-room', room })}>
                {t('taggingScaffold.delete')}
              </Button>
              {!room.archivedAt && (
                <Button size="sm" onClick={() => openDialog({ kind: 'create-power-point', roomId: room.id })}>
                  {t('taggingScaffold.addPowerPoint')}
                </Button>
              )}
            </div>

            <div className="mt-2 flex flex-col gap-2 pl-4">
              {(powerPointsByRoom.get(room.id) ?? []).map((powerPoint) => (
                <details key={powerPoint.id} className="rounded-md border border-border p-2">
                  <summary className="cursor-pointer font-medium">
                    {powerPoint.name} {powerPoint.archivedAt && <ArchivedBadge label={archivedBadgeLabel} />}
                  </summary>

                  <div className="mt-2 flex flex-wrap gap-2">
                    <Button size="sm" variant="outline" onClick={() => openDialog({ kind: 'rename-power-point', powerPoint })}>
                      {t('taggingScaffold.rename')}
                    </Button>
                    <Button size="sm" variant="outline" onClick={() => openDialog({ kind: 'delete-power-point', powerPoint })}>
                      {t('taggingScaffold.delete')}
                    </Button>
                    {!powerPoint.archivedAt && (
                      <Button size="sm" onClick={() => openDialog({ kind: 'create-device', powerPointId: powerPoint.id })}>
                        {t('taggingScaffold.addDevice')}
                      </Button>
                    )}
                  </div>

                  <div className="mt-2 flex flex-col gap-2 pl-4">
                    {(devicesByPowerPoint.get(powerPoint.id) ?? []).map((device) => (
                      <div key={device.id} className="flex items-center justify-between gap-2 rounded-md border border-border p-2">
                        <span>
                          {device.name} {device.archivedAt && <ArchivedBadge label={archivedBadgeLabel} />}
                        </span>
                        <div className="flex gap-2">
                          <Button size="sm" variant="outline" onClick={() => openDialog({ kind: 'rename-device', device })}>
                            {t('taggingScaffold.rename')}
                          </Button>
                          <Button size="sm" variant="outline" onClick={() => openDialog({ kind: 'delete-device', device })}>
                            {t('taggingScaffold.delete')}
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
      </div>

      <Dialog open={dialog !== null} onOpenChange={(open) => !open && !submitting && closeDialog()}>
        <DialogContent>
          {dialog?.kind === 'delete-room' && (
            <>
              <DialogHeader>
                <DialogTitle>{t('taggingScaffold.delete')}</DialogTitle>
                <DialogDescription>{t('taggingScaffold.confirmDeleteRoom')}</DialogDescription>
              </DialogHeader>
              {dialogError && <p className="text-destructive text-sm">{dialogError}</p>}
              <DialogFooter>
                <Button variant="outline" onClick={closeDialog} disabled={submitting}>
                  {t('taggingScaffold.cancel')}
                </Button>
                <Button variant="destructive" onClick={handleDelete} disabled={submitting}>
                  {t('taggingScaffold.delete')}
                </Button>
              </DialogFooter>
            </>
          )}

          {dialog?.kind === 'delete-power-point' && (
            <>
              <DialogHeader>
                <DialogTitle>{t('taggingScaffold.delete')}</DialogTitle>
                <DialogDescription>{t('taggingScaffold.confirmDeletePowerPoint')}</DialogDescription>
              </DialogHeader>
              {dialogError && <p className="text-destructive text-sm">{dialogError}</p>}
              <DialogFooter>
                <Button variant="outline" onClick={closeDialog} disabled={submitting}>
                  {t('taggingScaffold.cancel')}
                </Button>
                <Button variant="destructive" onClick={handleDelete} disabled={submitting}>
                  {t('taggingScaffold.delete')}
                </Button>
              </DialogFooter>
            </>
          )}

          {dialog?.kind === 'delete-device' && (
            <>
              <DialogHeader>
                <DialogTitle>{t('taggingScaffold.delete')}</DialogTitle>
                <DialogDescription>{t('taggingScaffold.confirmDeleteDevice')}</DialogDescription>
              </DialogHeader>
              {dialogError && <p className="text-destructive text-sm">{dialogError}</p>}
              <DialogFooter>
                <Button variant="outline" onClick={closeDialog} disabled={submitting}>
                  {t('taggingScaffold.cancel')}
                </Button>
                <Button variant="destructive" onClick={handleDelete} disabled={submitting}>
                  {t('taggingScaffold.delete')}
                </Button>
              </DialogFooter>
            </>
          )}

          {dialog &&
            dialog.kind !== 'delete-room' &&
            dialog.kind !== 'delete-power-point' &&
            dialog.kind !== 'delete-device' && (
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
                  <Button type="submit" disabled={submitting || !nameInput.trim()}>
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
