import { useEffect, useState } from 'react'
import { apiFetch } from '@/lib/api-client'
import type { DeviceDto, PowerPointDto, RoomDto } from './tagging-scaffold-types'

// Fetches the whole Room → Power Point → Device tagging scaffold (AC #1-#4). Three
// parallel GETs, no combined endpoint — unneeded complexity at this data scale (a household's
// Room/PowerPoint/Device count is small, dozens not thousands).
export function useTaggingScaffoldData() {
  const [rooms, setRooms] = useState<RoomDto[]>([])
  const [powerPoints, setPowerPoints] = useState<PowerPointDto[]>([])
  const [devices, setDevices] = useState<DeviceDto[]>([])
  const [loading, setLoading] = useState(true)
  const [loadError, setLoadError] = useState(false)

  useEffect(() => {
    let cancelled = false

    Promise.all([
      apiFetch('/api/rooms'),
      apiFetch('/api/power-points'),
      apiFetch('/api/devices'),
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

  return { rooms, setRooms, powerPoints, setPowerPoints, devices, setDevices, loading, loadError }
}
