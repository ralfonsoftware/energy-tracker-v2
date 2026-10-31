import { useRef, useState, type Dispatch, type FormEvent, type SetStateAction } from 'react'
import { useTranslation } from 'react-i18next'
import { ApiError, apiFetch, toApiError } from '@/lib/api-client'
import type { DeviceDto, DialogState, PowerPointDto, RoomDto } from './tagging-scaffold-types'

// The dialog state of the manager and the three mutations it drives (create/rename, archive, move).
// Each mutation writes the server's answer back through the setters of the loaded scaffold.
export function useTaggingScaffoldDialog({
  setRooms,
  setPowerPoints,
  setDevices,
}: {
  setRooms: Dispatch<SetStateAction<RoomDto[]>>
  setPowerPoints: Dispatch<SetStateAction<PowerPointDto[]>>
  setDevices: Dispatch<SetStateAction<DeviceDto[]>>
}) {
  const { t } = useTranslation()
  const [dialog, setDialog] = useState<DialogState | null>(null)
  const [nameInput, setNameInput] = useState('')
  const [submitting, setSubmitting] = useState(false)
  const [dialogError, setDialogError] = useState<string | null>(null)

  // Tracks the live `dialog` value outside of React's closure/batching timing so a request that
  // resolves after the user has already closed and reopened a different dialog can tell it's
  // stale and skip clobbering that newer dialog's state.
  const dialogRef = useRef<DialogState | null>(null)
  dialogRef.current = dialog

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
          const response = await apiFetch('/api/rooms', {
            method: 'POST',
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
          const response = await apiFetch(`/api/rooms/${dialog.room.id}`, {
            method: 'PUT',
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
          const response = await apiFetch('/api/power-points', {
            method: 'POST',
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
          const response = await apiFetch(`/api/power-points/${dialog.powerPoint.id}`, {
            method: 'PUT',
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
          const response = await apiFetch('/api/devices', {
            method: 'POST',
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
          const response = await apiFetch(`/api/devices/${dialog.device.id}`, {
            method: 'PUT',
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
        const response = await apiFetch(`/api/rooms/${dialog.room.id}`, { method: 'DELETE' })
        if (!response.ok) {
          throw await toApiError(response)
        }
        const room = (await response.json()) as RoomDto
        setRooms((current) => current.map((r) => (r.id === room.id ? room : r)))
      } else if (dialog.kind === 'delete-power-point') {
        const response = await apiFetch(`/api/power-points/${dialog.powerPoint.id}`, { method: 'DELETE' })
        if (!response.ok) {
          throw await toApiError(response)
        }
        const powerPoint = (await response.json()) as PowerPointDto
        setPowerPoints((current) => current.map((p) => (p.id === powerPoint.id ? powerPoint : p)))
      } else if (dialog.kind === 'delete-device') {
        const response = await apiFetch(`/api/devices/${dialog.device.id}`, { method: 'DELETE' })
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
        const response = await apiFetch(`/api/power-points/${dialog.powerPoint.id}/room`, {
          method: 'PUT',
          headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify({ roomId: destinationId }),
        })
        if (!response.ok) {
          throw await toApiError(response)
        }
        const powerPoint = (await response.json()) as PowerPointDto
        setPowerPoints((current) => current.map((p) => (p.id === powerPoint.id ? powerPoint : p)))
      } else {
        const response = await apiFetch(`/api/devices/${dialog.device.id}/power-point`, {
          method: 'PUT',
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

  return {
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
  }
}
