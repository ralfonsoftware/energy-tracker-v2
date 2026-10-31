export interface RoomDto {
  id: string
  name: string
  archivedAt: string | null
}

export interface PowerPointDto {
  id: string
  roomId: string
  name: string
  archivedAt: string | null
}

export interface DeviceDto {
  id: string
  powerPointId: string
  name: string
  archivedAt: string | null
}

export type DialogState =
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
