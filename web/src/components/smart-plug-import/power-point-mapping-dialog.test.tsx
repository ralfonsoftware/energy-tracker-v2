import { render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { PowerPointMappingDialog } from './power-point-mapping-dialog'

function jsonResponse(body: object | null, status = 200) {
  return new Response(body === null ? null : JSON.stringify(body), { status })
}

const ROOM = { id: 'room-1', name: 'Living room', archivedAt: null }
const EXISTING_POWER_POINT = { id: 'pp-1', roomId: 'room-1', name: 'Desk lamp', archivedAt: null }

function stubDataFetch(overrides: Partial<Record<string, () => Promise<Response>>> = {}) {
  const fetchMock = vi.fn((url: string, init?: RequestInit) => {
    if (url === '/api/rooms' && overrides.rooms) {
      return overrides.rooms()
    }
    if (url === '/api/rooms') {
      return Promise.resolve(jsonResponse([ROOM]))
    }
    if (url === '/api/power-points' && init?.method === 'POST' && overrides.createPowerPoint) {
      return overrides.createPowerPoint()
    }
    if (url === '/api/power-points' && init?.method === 'POST') {
      return Promise.resolve(jsonResponse({ id: 'pp-new', roomId: ROOM.id, name: 'Office Desk', archivedAt: null }))
    }
    if (url === '/api/power-points' && overrides.powerPoints) {
      return overrides.powerPoints()
    }
    if (url === '/api/power-points') {
      return Promise.resolve(jsonResponse([EXISTING_POWER_POINT]))
    }
    if (url.includes('/power-point-mapping') && overrides.mapping) {
      return overrides.mapping()
    }
    if (url.includes('/power-point-mapping')) {
      return Promise.resolve(jsonResponse({ id: 'import-1', status: 'completed' }))
    }

    throw new Error(`Unexpected fetch: ${url}`)
  })
  vi.stubGlobal('fetch', fetchMock)
  return fetchMock
}

describe('PowerPointMappingDialog', () => {
  afterEach(() => {
    vi.unstubAllGlobals()
  })

  it('renders the title with the device tag and the existing Power Point as a mappable row', async () => {
    stubDataFetch()
    render(<PowerPointMappingDialog smartPlugImportId="import-1" deviceTag="Office Desk" onMapped={vi.fn()} onCancel={vi.fn()} />)

    expect(screen.getByText('New Power Point: "Office Desk"')).toBeInTheDocument()
    await waitFor(() => expect(screen.getByText('Living room → Desk lamp')).toBeInTheDocument())
  })

  it('maps to an existing Power Point directly on row tap', async () => {
    const fetchMock = stubDataFetch()
    const onMapped = vi.fn()
    render(<PowerPointMappingDialog smartPlugImportId="import-1" deviceTag="Office Desk" onMapped={onMapped} onCancel={vi.fn()} />)

    await waitFor(() => expect(screen.getByText('Living room → Desk lamp')).toBeInTheDocument())
    await userEvent.click(screen.getByText('Living room → Desk lamp'))

    await waitFor(() => expect(onMapped).toHaveBeenCalledTimes(1))
    const mappingCall = fetchMock.mock.calls.find(([url]) => (url as string).includes('/power-point-mapping'))
    expect(mappingCall).toBeDefined()
    const [url, init] = mappingCall!
    expect(url).toBe('/api/smart-plug-imports/import-1/power-point-mapping')
    expect(JSON.parse((init as RequestInit).body as string)).toEqual({ powerPointId: 'pp-1' })
  })

  it('creates a new Power Point (name pre-filled to the device tag, editable) in the pre-selected Room, then maps it', async () => {
    const fetchMock = stubDataFetch()
    const onMapped = vi.fn()
    render(<PowerPointMappingDialog smartPlugImportId="import-1" deviceTag="Office Desk" onMapped={onMapped} onCancel={vi.fn()} />)

    await waitFor(() => expect(screen.getByDisplayValue('Office Desk')).toBeInTheDocument())

    await userEvent.click(screen.getByRole('button', { name: 'Create Power Point "Office Desk"' }))

    await waitFor(() => expect(onMapped).toHaveBeenCalledTimes(1))
    const createCall = fetchMock.mock.calls.find(
      ([url, init]) => url === '/api/power-points' && (init as RequestInit | undefined)?.method === 'POST',
    )
    expect(createCall).toBeDefined()
    expect(JSON.parse((createCall![1] as RequestInit).body as string)).toEqual({ roomId: 'room-1', name: 'Office Desk' })
  })

  it('requires a Room picker even though the mockup has none, since PowerPoint.RoomId is non-nullable', async () => {
    stubDataFetch()
    render(<PowerPointMappingDialog smartPlugImportId="import-1" deviceTag="Office Desk" onMapped={vi.fn()} onCancel={vi.fn()} />)

    await waitFor(() => expect(screen.getByRole('combobox')).toBeInTheDocument())
    expect(screen.getByRole('option', { name: 'Living room' })).toBeInTheDocument()
  })

  it('surfaces a duplicate-name error from CreatePowerPoint inline rather than closing the dialog', async () => {
    const onMapped = vi.fn()
    stubDataFetch({
      createPowerPoint: () =>
        Promise.resolve(jsonResponse({ detail: "A Power Point named 'Office Desk' already exists in this Room." }, 400)),
    })
    render(<PowerPointMappingDialog smartPlugImportId="import-1" deviceTag="Office Desk" onMapped={onMapped} onCancel={vi.fn()} />)

    await waitFor(() => expect(screen.getByDisplayValue('Office Desk')).toBeInTheDocument())
    await userEvent.click(screen.getByRole('button', { name: 'Create Power Point "Office Desk"' }))

    await waitFor(() =>
      expect(screen.getByText("A Power Point named 'Office Desk' already exists in this Room.")).toBeInTheDocument(),
    )
    expect(onMapped).not.toHaveBeenCalled()
  })

  it('surfaces a mapping error inline without closing the dialog', async () => {
    const onMapped = vi.fn()
    stubDataFetch({ mapping: () => Promise.resolve(jsonResponse({ detail: 'Conflict' }, 409)) })
    render(<PowerPointMappingDialog smartPlugImportId="import-1" deviceTag="Office Desk" onMapped={onMapped} onCancel={vi.fn()} />)

    await waitFor(() => expect(screen.getByText('Living room → Desk lamp')).toBeInTheDocument())
    await userEvent.click(screen.getByText('Living room → Desk lamp'))

    await waitFor(() => expect(screen.getByText('Conflict')).toBeInTheDocument())
    expect(onMapped).not.toHaveBeenCalled()
  })

  it('excludes archived Power Points from the mappable list', async () => {
    stubDataFetch({
      powerPoints: () =>
        Promise.resolve(
          jsonResponse([
            EXISTING_POWER_POINT,
            { id: 'pp-2', roomId: 'room-1', name: 'Old plug', archivedAt: '2026-01-01T00:00:00+00:00' },
          ]),
        ),
    })
    render(<PowerPointMappingDialog smartPlugImportId="import-1" deviceTag="Office Desk" onMapped={vi.fn()} onCancel={vi.fn()} />)

    await waitFor(() => expect(screen.getByText('Living room → Desk lamp')).toBeInTheDocument())
    expect(screen.queryByText(/Old plug/)).not.toBeInTheDocument()
  })

  it('adds the newly-created Power Point to the mappable list immediately, so a failed mapping call is recoverable', async () => {
    stubDataFetch({ mapping: () => Promise.resolve(jsonResponse({ detail: 'Conflict' }, 409)) })
    render(<PowerPointMappingDialog smartPlugImportId="import-1" deviceTag="Office Desk" onMapped={vi.fn()} onCancel={vi.fn()} />)

    await waitFor(() => expect(screen.getByDisplayValue('Office Desk')).toBeInTheDocument())
    await userEvent.click(screen.getByRole('button', { name: 'Create Power Point "Office Desk"' }))

    await waitFor(() => expect(screen.getByText('Conflict')).toBeInTheDocument())
    // The create call itself succeeded (returns pp-new in Living room) — it must now be tappable
    // in the existing-Power-Point list even though the follow-up mapping call failed.
    expect(screen.getByText('Living room → Office Desk')).toBeInTheDocument()
  })

  it('calls onCancel when the dialog is dismissed via its close control', async () => {
    stubDataFetch()
    const onCancel = vi.fn()
    render(<PowerPointMappingDialog smartPlugImportId="import-1" deviceTag="Office Desk" onMapped={vi.fn()} onCancel={onCancel} />)

    await waitFor(() => expect(screen.getByText('Living room → Desk lamp')).toBeInTheDocument())
    await userEvent.click(screen.getByRole('button', { name: 'Close' }))

    expect(onCancel).toHaveBeenCalledTimes(1)
  })

  it('shows a retry action on a load failure, which reloads Rooms and Power Points', async () => {
    let roomsCallCount = 0
    stubDataFetch({
      rooms: () => {
        roomsCallCount += 1
        return roomsCallCount === 1 ? Promise.reject(new Error('network blip')) : Promise.resolve(jsonResponse([ROOM]))
      },
    })
    render(<PowerPointMappingDialog smartPlugImportId="import-1" deviceTag="Office Desk" onMapped={vi.fn()} onCancel={vi.fn()} />)

    await waitFor(() => expect(screen.getByRole('button', { name: 'Retry' })).toBeInTheDocument())
    await userEvent.click(screen.getByRole('button', { name: 'Retry' }))

    await waitFor(() => expect(screen.getByText('Living room → Desk lamp')).toBeInTheDocument())
  })

  it('shows a message instead of the Room picker when the Household has no active Rooms', async () => {
    stubDataFetch({ rooms: () => Promise.resolve(jsonResponse([])) })
    render(<PowerPointMappingDialog smartPlugImportId="import-1" deviceTag="Office Desk" onMapped={vi.fn()} onCancel={vi.fn()} />)

    await waitFor(() =>
      expect(
        screen.getByText("You don't have any Rooms yet. Add one in Settings before creating a Power Point here."),
      ).toBeInTheDocument(),
    )
    expect(screen.queryByRole('combobox')).not.toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Create Power Point "Office Desk"' })).toBeDisabled()
  })

  it('caps the existing Power Point list to a scrollable region, with every row still clickable', async () => {
    // 30 rows is well beyond the ~6-7 that fit in the list's max-h-64 cap, so this exercises the
    // overflow case the fix targets rather than a list that happens to fit without scrolling.
    const manyPowerPoints = Array.from({ length: 30 }, (_, index) => ({
      id: `pp-${index}`,
      roomId: ROOM.id,
      name: `Plug ${index}`,
      archivedAt: null,
    }))
    const fetchMock = stubDataFetch({ powerPoints: () => Promise.resolve(jsonResponse(manyPowerPoints)) })
    render(<PowerPointMappingDialog smartPlugImportId="import-1" deviceTag="Office Desk" onMapped={vi.fn()} onCancel={vi.fn()} />)

    const lastRow = await screen.findByText('Living room → Plug 29')
    const list = lastRow.closest('div.overflow-y-auto')
    expect(list).not.toBeNull()
    expect(list).toHaveClass('max-h-64')

    await userEvent.click(lastRow)

    await waitFor(() => {
      const mappingCall = fetchMock.mock.calls.find(([url]) => (url as string).includes('/power-point-mapping'))
      expect(mappingCall).toBeDefined()
      expect(JSON.parse((mappingCall![1] as RequestInit).body as string)).toEqual({ powerPointId: 'pp-29' })
    })
  })
})
