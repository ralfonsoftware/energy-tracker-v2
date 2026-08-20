import { useCallback, useEffect, useRef, useState } from 'react'
import { useTranslation } from 'react-i18next'
import { Button } from '@/components/ui/button'
import { Dialog, DialogContent, DialogHeader, DialogTitle } from '@/components/ui/dialog'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import { GLASS_MODAL_CLASSNAME } from '@/lib/glass-classnames'
import {
  ApiError,
  createPowerPoint,
  fetchPowerPoints,
  fetchRooms,
  mapSmartPlugImportToPowerPoint,
  type PowerPointDto,
  type RoomDto,
} from '@/lib/smart-plug-import-api'

// Mockup reference: key-smart-plug-import.html State 3 (lines 409-449) — title, body copy,
// primary "Create Power Point" action, "or map to an existing one" divider, tappable list of
// existing Power Points. Neutral glass-dialog language (not destructive/red), matching the Meter
// Regression prompt's modal pattern — an unmatched tag is an expected step, not an error.
export function PowerPointMappingDialog({
  smartPlugImportId,
  deviceTag,
  onMapped,
  onCancel,
}: {
  smartPlugImportId: string
  deviceTag: string
  onMapped: () => void
  onCancel: () => void
}) {
  const { t } = useTranslation()
  const [rooms, setRooms] = useState<RoomDto[]>([])
  const [powerPoints, setPowerPoints] = useState<PowerPointDto[]>([])
  const [loading, setLoading] = useState(true)
  const [loadError, setLoadError] = useState(false)
  const [nameInput, setNameInput] = useState(deviceTag)
  // PowerPoint.RoomId is non-nullable — the mockup's create button has no Room picker, but the
  // schema requires one. A minimal native <select>, defaulting to the first non-archived Room, is
  // a deliberate addition beyond the literal mock rather than a missed mock detail.
  const [selectedRoomId, setSelectedRoomId] = useState('')
  const [submitting, setSubmitting] = useState(false)
  const [error, setError] = useState<string | null>(null)
  // The Close (X) button DialogContent always renders, and the fetch below, can now outlive the
  // component (user cancels mid-request) — guard every state write against a stale callback.
  const mountedRef = useRef(true)
  useEffect(() => {
    mountedRef.current = true
    return () => {
      mountedRef.current = false
    }
  }, [])

  const loadData = useCallback(() => {
    setLoading(true)
    setLoadError(false)
    Promise.all([fetchRooms(), fetchPowerPoints()])
      .then(([roomsData, powerPointsData]) => {
        if (!mountedRef.current) {
          return
        }

        setRooms(roomsData)
        setPowerPoints(powerPointsData)
        setSelectedRoomId((current) => current || roomsData.find((room) => !room.archivedAt)?.id || '')
      })
      .catch(() => {
        if (mountedRef.current) {
          setLoadError(true)
        }
      })
      .finally(() => {
        if (mountedRef.current) {
          setLoading(false)
        }
      })
  }, [])

  useEffect(() => {
    loadData()
  }, [loadData])

  const handleMapToExisting = async (powerPointId: string) => {
    setSubmitting(true)
    setError(null)
    try {
      await mapSmartPlugImportToPowerPoint(smartPlugImportId, powerPointId)
      onMapped()
    } catch (err) {
      if (mountedRef.current) {
        setError(err instanceof ApiError && err.detail ? err.detail : t('smartPlugImport.mappingModal.mapError'))
      }
    } finally {
      if (mountedRef.current) {
        setSubmitting(false)
      }
    }
  }

  const handleCreateAndMap = async () => {
    setSubmitting(true)
    setError(null)
    try {
      const powerPoint = await createPowerPoint(selectedRoomId, nameInput)
      // Append immediately, before attempting the mapping call — if that call fails below, this
      // is the only way back into the flow: the "map to an existing one" list must already show
      // the Power Point that was just created (Dev Notes' recoverable-two-step design).
      if (mountedRef.current) {
        setPowerPoints((current) => [...current, powerPoint])
      }
      await mapSmartPlugImportToPowerPoint(smartPlugImportId, powerPoint.id)
      onMapped()
    } catch (err) {
      if (mountedRef.current) {
        setError(err instanceof ApiError && err.detail ? err.detail : t('smartPlugImport.mappingModal.mapError'))
      }
    } finally {
      if (mountedRef.current) {
        setSubmitting(false)
      }
    }
  }

  const roomsById = new Map(rooms.map((room) => [room.id, room]))
  const activeRooms = rooms.filter((room) => !room.archivedAt)
  const activePowerPoints = powerPoints.filter((powerPoint) => !powerPoint.archivedAt)

  return (
    <Dialog open onOpenChange={(open) => !open && !submitting && onCancel()}>
      <DialogContent className={GLASS_MODAL_CLASSNAME}>
        <DialogHeader>
          <DialogTitle>{t('smartPlugImport.mappingModal.title', { deviceTag })}</DialogTitle>
        </DialogHeader>

        <p className="text-muted-foreground text-sm">{t('smartPlugImport.mappingModal.body')}</p>

        {loading && <p className="text-muted-foreground text-sm">{t('taggingScaffold.loading')}</p>}

        {!loading && !loadError && (
          <>
            <div className="flex flex-col gap-2">
              <Label htmlFor="mapping-room">{t('smartPlugImport.mappingModal.roomLabel')}</Label>
              {activeRooms.length === 0 ? (
                <p className="text-muted-foreground text-sm">{t('smartPlugImport.mappingModal.noRooms')}</p>
              ) : (
                <select
                  id="mapping-room"
                  className="border-input h-9 rounded-md border bg-transparent px-3 text-sm"
                  value={selectedRoomId}
                  onChange={(event) => setSelectedRoomId(event.target.value)}
                  disabled={submitting}
                >
                  {activeRooms.map((room) => (
                    <option key={room.id} value={room.id}>
                      {room.name}
                    </option>
                  ))}
                </select>
              )}
              <Input
                id="mapping-power-point-name"
                aria-label={t('taggingScaffold.namePlaceholder')}
                value={nameInput}
                onChange={(event) => setNameInput(event.target.value)}
                required
              />
              <Button
                type="button"
                variant="glass-primary"
                onClick={handleCreateAndMap}
                disabled={submitting || !nameInput.trim() || !selectedRoomId}
              >
                {t('smartPlugImport.mappingModal.createButton', { deviceTag: nameInput })}
              </Button>
            </div>

            <div className="text-muted-foreground text-center text-xs uppercase">
              {t('smartPlugImport.mappingModal.orDivider')}
            </div>

            <div className="flex flex-col gap-1">
              {activePowerPoints.length === 0 && (
                <p className="text-muted-foreground text-sm">{t('smartPlugImport.mappingModal.noExisting')}</p>
              )}
              {activePowerPoints.map((powerPoint) => (
                <Button
                  key={powerPoint.id}
                  type="button"
                  variant="ghost"
                  className="justify-between"
                  disabled={submitting}
                  onClick={() => handleMapToExisting(powerPoint.id)}
                >
                  {`${roomsById.get(powerPoint.roomId)?.name ?? ''} → ${powerPoint.name}`}
                </Button>
              ))}
            </div>
          </>
        )}

        {loadError && (
          <div className="flex flex-col gap-2">
            <p className="text-destructive text-sm">{t('taggingScaffold.errorGeneric')}</p>
            <Button type="button" variant="outline" size="sm" onClick={loadData}>
              {t('smartPlugImport.mappingModal.retry')}
            </Button>
          </div>
        )}
        {error && <p className="text-destructive text-sm">{error}</p>}
      </DialogContent>
    </Dialog>
  )
}
