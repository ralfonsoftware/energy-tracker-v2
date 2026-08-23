import { render, screen, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { TaggingScaffoldManager } from './tagging-scaffold-manager'

function jsonResponse(body: object | null, status = 200) {
  return new Response(body === null ? null : JSON.stringify(body), { status })
}

// Routes fetch calls by (method, URL) pair — matches App.test.tsx's mockFetchRoutes pattern.
// POST/PUT routes here are functions of the request body so tests can assert what was sent and
// vary the mocked response (e.g. simulate a 409 race on an otherwise-active parent).
function mockFetchRoutes(
  routes: Array<{ method: string; url: string; respond: (body: unknown) => Response }>,
) {
  const fetchMock = vi.fn((input: string | URL | Request, init?: RequestInit) => {
    const url = String(input)
    const method = (init?.method ?? 'GET').toUpperCase()
    const route = routes.find((r) => r.method === method && r.url === url)
    if (!route) {
      throw new Error(`Unmocked fetch: ${method} ${url}`)
    }

    const body = init?.body ? JSON.parse(init.body as string) : undefined
    return Promise.resolve(route.respond(body))
  })
  vi.stubGlobal('fetch', fetchMock)
  return fetchMock
}

const emptyScaffoldRoutes = [
  { method: 'GET', url: '/api/rooms', respond: () => jsonResponse([]) },
  { method: 'GET', url: '/api/power-points', respond: () => jsonResponse([]) },
  { method: 'GET', url: '/api/devices', respond: () => jsonResponse([]) },
]

describe('TaggingScaffoldManager', () => {
  afterEach(() => {
    vi.unstubAllGlobals()
  })

  it('renders the empty state when the Household has no Rooms yet', async () => {
    mockFetchRoutes(emptyScaffoldRoutes)

    render(<TaggingScaffoldManager />)

    expect(await screen.findByText('No rooms yet.')).toBeInTheDocument()
  })

  it('renders the Room -> Power Point -> Device tree from mocked GET responses', async () => {
    mockFetchRoutes([
      { method: 'GET', url: '/api/rooms', respond: () => jsonResponse([{ id: 'r1', name: 'Kitchen', archivedAt: null }]) },
      { method: 'GET', url: '/api/power-points', respond: () => jsonResponse([{ id: 'p1', roomId: 'r1', name: 'Counter outlet', archivedAt: null }]) },
      { method: 'GET', url: '/api/devices', respond: () => jsonResponse([{ id: 'd1', powerPointId: 'p1', name: 'Kettle', archivedAt: null }]) },
    ])
    const user = userEvent.setup()

    render(<TaggingScaffoldManager />)

    const roomSummary = await screen.findByText('Kitchen', { exact: false })
    await user.click(roomSummary)
    const powerPointSummary = await screen.findByText('Counter outlet', { exact: false })
    await user.click(powerPointSummary)

    expect(await screen.findByText('Kettle', { exact: false })).toBeInTheDocument()
  })

  it('creating a Room issues the expected POST and adds it to the rendered tree', async () => {
    mockFetchRoutes([
      ...emptyScaffoldRoutes,
      {
        method: 'POST',
        url: '/api/rooms',
        respond: (body) => {
          expect(body).toEqual({ name: 'Bathroom' })
          return jsonResponse({ id: 'r2', name: 'Bathroom', archivedAt: null })
        },
      },
    ])
    const user = userEvent.setup()

    render(<TaggingScaffoldManager />)
    await screen.findByText('No rooms yet.')

    await user.click(screen.getByRole('button', { name: 'Add Room' }))
    await user.type(screen.getByLabelText('Name'), 'Bathroom')
    await user.click(screen.getByRole('button', { name: 'Save' }))

    expect(await screen.findByText('Bathroom', { exact: false })).toBeInTheDocument()
  })

  it('archiving a Room shows the archived badge and hides the add-Power-Point action', async () => {
    mockFetchRoutes([
      { method: 'GET', url: '/api/rooms', respond: () => jsonResponse([{ id: 'r1', name: 'Kitchen', archivedAt: null }]) },
      { method: 'GET', url: '/api/power-points', respond: () => jsonResponse([]) },
      { method: 'GET', url: '/api/devices', respond: () => jsonResponse([]) },
      {
        method: 'DELETE',
        url: '/api/rooms/r1',
        respond: () => jsonResponse({ id: 'r1', name: 'Kitchen', archivedAt: '2026-01-01T00:00:00Z' }),
      },
    ])
    const user = userEvent.setup()

    render(<TaggingScaffoldManager />)
    await user.click(await screen.findByText('Kitchen', { exact: false }))

    expect(screen.getByRole('button', { name: 'Add Power Point' })).toBeInTheDocument()

    const deleteButtons = screen.getAllByRole('button', { name: 'Delete' })
    await user.click(deleteButtons[0])
    await user.click(screen.getByRole('button', { name: 'Archive' }))

    // Hidden by default after archiving (AC #1/#2) — reveal it to assert the badge/actions.
    await user.click(screen.getByRole('button', { name: 'Show archived items' }))

    expect(await screen.findByText('Archived')).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Add Power Point' })).not.toBeInTheDocument()
  })

  it('renaming a Room issues the expected PUT and updates the rendered tree', async () => {
    mockFetchRoutes([
      { method: 'GET', url: '/api/rooms', respond: () => jsonResponse([{ id: 'r1', name: 'Kitchen', archivedAt: null }]) },
      { method: 'GET', url: '/api/power-points', respond: () => jsonResponse([]) },
      { method: 'GET', url: '/api/devices', respond: () => jsonResponse([]) },
      {
        method: 'PUT',
        url: '/api/rooms/r1',
        respond: (body) => {
          expect(body).toEqual({ name: 'Scullery' })
          return jsonResponse({ id: 'r1', name: 'Scullery', archivedAt: null })
        },
      },
    ])
    const user = userEvent.setup()

    render(<TaggingScaffoldManager />)
    await user.click(await screen.findByText('Kitchen', { exact: false }))

    await user.click(screen.getAllByRole('button', { name: 'Rename' })[0])
    const nameInput = screen.getByLabelText('Name')
    await user.clear(nameInput)
    await user.type(nameInput, 'Scullery')
    await user.click(screen.getByRole('button', { name: 'Save' }))

    expect(await screen.findByText('Scullery', { exact: false })).toBeInTheDocument()
    expect(screen.queryByText('Kitchen', { exact: false })).not.toBeInTheDocument()
  })

  it('creating a Power Point issues the expected POST and adds it to the rendered tree', async () => {
    mockFetchRoutes([
      { method: 'GET', url: '/api/rooms', respond: () => jsonResponse([{ id: 'r1', name: 'Kitchen', archivedAt: null }]) },
      { method: 'GET', url: '/api/power-points', respond: () => jsonResponse([]) },
      { method: 'GET', url: '/api/devices', respond: () => jsonResponse([]) },
      {
        method: 'POST',
        url: '/api/power-points',
        respond: (body) => {
          expect(body).toEqual({ roomId: 'r1', name: 'Counter outlet' })
          return jsonResponse({ id: 'p1', roomId: 'r1', name: 'Counter outlet', archivedAt: null })
        },
      },
    ])
    const user = userEvent.setup()

    render(<TaggingScaffoldManager />)
    await user.click(await screen.findByText('Kitchen', { exact: false }))

    await user.click(screen.getByRole('button', { name: 'Add Power Point' }))
    await user.type(screen.getByLabelText('Name'), 'Counter outlet')
    await user.click(screen.getByRole('button', { name: 'Save' }))

    expect(await screen.findByText('Counter outlet', { exact: false })).toBeInTheDocument()
  })

  it('renaming a Power Point issues the expected PUT and updates the rendered tree', async () => {
    mockFetchRoutes([
      { method: 'GET', url: '/api/rooms', respond: () => jsonResponse([{ id: 'r1', name: 'Kitchen', archivedAt: null }]) },
      { method: 'GET', url: '/api/power-points', respond: () => jsonResponse([{ id: 'p1', roomId: 'r1', name: 'Counter outlet', archivedAt: null }]) },
      { method: 'GET', url: '/api/devices', respond: () => jsonResponse([]) },
      {
        method: 'PUT',
        url: '/api/power-points/p1',
        respond: (body) => {
          expect(body).toEqual({ name: 'Island outlet' })
          return jsonResponse({ id: 'p1', roomId: 'r1', name: 'Island outlet', archivedAt: null })
        },
      },
    ])
    const user = userEvent.setup()

    render(<TaggingScaffoldManager />)
    await user.click(await screen.findByText('Kitchen', { exact: false }))
    await user.click(await screen.findByText('Counter outlet', { exact: false }))

    await user.click(screen.getAllByRole('button', { name: 'Rename' })[1])
    const nameInput = screen.getByLabelText('Name')
    await user.clear(nameInput)
    await user.type(nameInput, 'Island outlet')
    await user.click(screen.getByRole('button', { name: 'Save' }))

    expect(await screen.findByText('Island outlet', { exact: false })).toBeInTheDocument()
    expect(screen.queryByText('Counter outlet', { exact: false })).not.toBeInTheDocument()
  })

  it('archiving a Power Point shows the archived badge and hides the add-Device action', async () => {
    mockFetchRoutes([
      { method: 'GET', url: '/api/rooms', respond: () => jsonResponse([{ id: 'r1', name: 'Kitchen', archivedAt: null }]) },
      { method: 'GET', url: '/api/power-points', respond: () => jsonResponse([{ id: 'p1', roomId: 'r1', name: 'Counter outlet', archivedAt: null }]) },
      { method: 'GET', url: '/api/devices', respond: () => jsonResponse([]) },
      {
        method: 'DELETE',
        url: '/api/power-points/p1',
        respond: () => jsonResponse({ id: 'p1', roomId: 'r1', name: 'Counter outlet', archivedAt: '2026-01-01T00:00:00Z' }),
      },
    ])
    const user = userEvent.setup()

    render(<TaggingScaffoldManager />)
    await user.click(await screen.findByText('Kitchen', { exact: false }))
    await user.click(await screen.findByText('Counter outlet', { exact: false }))

    expect(screen.getByRole('button', { name: 'Add Device' })).toBeInTheDocument()

    const deleteButtons = screen.getAllByRole('button', { name: 'Delete' })
    await user.click(deleteButtons[1])
    await user.click(screen.getByRole('button', { name: 'Archive' }))

    // Hidden by default after archiving (AC #1/#2) — reveal it to assert the badge/actions.
    await user.click(screen.getByRole('button', { name: 'Show archived items' }))

    expect(await screen.findByText('Archived')).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Add Device' })).not.toBeInTheDocument()
  })

  it('creating a Device issues the expected POST and adds it to the rendered tree', async () => {
    mockFetchRoutes([
      { method: 'GET', url: '/api/rooms', respond: () => jsonResponse([{ id: 'r1', name: 'Kitchen', archivedAt: null }]) },
      { method: 'GET', url: '/api/power-points', respond: () => jsonResponse([{ id: 'p1', roomId: 'r1', name: 'Counter outlet', archivedAt: null }]) },
      { method: 'GET', url: '/api/devices', respond: () => jsonResponse([]) },
      {
        method: 'POST',
        url: '/api/devices',
        respond: (body) => {
          expect(body).toEqual({ powerPointId: 'p1', name: 'Kettle' })
          return jsonResponse({ id: 'd1', powerPointId: 'p1', name: 'Kettle', archivedAt: null })
        },
      },
    ])
    const user = userEvent.setup()

    render(<TaggingScaffoldManager />)
    await user.click(await screen.findByText('Kitchen', { exact: false }))
    await user.click(await screen.findByText('Counter outlet', { exact: false }))

    await user.click(screen.getByRole('button', { name: 'Add Device' }))
    await user.type(screen.getByLabelText('Name'), 'Kettle')
    await user.click(screen.getByRole('button', { name: 'Save' }))

    expect(await screen.findByText('Kettle', { exact: false })).toBeInTheDocument()
  })

  it('renaming a Device issues the expected PUT and updates the rendered tree', async () => {
    mockFetchRoutes([
      { method: 'GET', url: '/api/rooms', respond: () => jsonResponse([{ id: 'r1', name: 'Kitchen', archivedAt: null }]) },
      { method: 'GET', url: '/api/power-points', respond: () => jsonResponse([{ id: 'p1', roomId: 'r1', name: 'Counter outlet', archivedAt: null }]) },
      { method: 'GET', url: '/api/devices', respond: () => jsonResponse([{ id: 'd1', powerPointId: 'p1', name: 'Kettle', archivedAt: null }]) },
      {
        method: 'PUT',
        url: '/api/devices/d1',
        respond: (body) => {
          expect(body).toEqual({ name: 'Toaster' })
          return jsonResponse({ id: 'd1', powerPointId: 'p1', name: 'Toaster', archivedAt: null })
        },
      },
    ])
    const user = userEvent.setup()

    render(<TaggingScaffoldManager />)
    await user.click(await screen.findByText('Kitchen', { exact: false }))
    await user.click(await screen.findByText('Counter outlet', { exact: false }))

    await user.click(screen.getAllByRole('button', { name: 'Rename' })[2])
    const nameInput = screen.getByLabelText('Name')
    await user.clear(nameInput)
    await user.type(nameInput, 'Toaster')
    await user.click(screen.getByRole('button', { name: 'Save' }))

    expect(await screen.findByText('Toaster', { exact: false })).toBeInTheDocument()
    expect(screen.queryByText('Kettle', { exact: false })).not.toBeInTheDocument()
  })

  it('archiving a Device shows the archived badge', async () => {
    mockFetchRoutes([
      { method: 'GET', url: '/api/rooms', respond: () => jsonResponse([{ id: 'r1', name: 'Kitchen', archivedAt: null }]) },
      { method: 'GET', url: '/api/power-points', respond: () => jsonResponse([{ id: 'p1', roomId: 'r1', name: 'Counter outlet', archivedAt: null }]) },
      { method: 'GET', url: '/api/devices', respond: () => jsonResponse([{ id: 'd1', powerPointId: 'p1', name: 'Kettle', archivedAt: null }]) },
      {
        method: 'DELETE',
        url: '/api/devices/d1',
        respond: () => jsonResponse({ id: 'd1', powerPointId: 'p1', name: 'Kettle', archivedAt: '2026-01-01T00:00:00Z' }),
      },
    ])
    const user = userEvent.setup()

    render(<TaggingScaffoldManager />)
    await user.click(await screen.findByText('Kitchen', { exact: false }))
    await user.click(await screen.findByText('Counter outlet', { exact: false }))

    const deleteButtons = screen.getAllByRole('button', { name: 'Delete' })
    await user.click(deleteButtons[2])
    await user.click(screen.getByRole('button', { name: 'Archive' }))

    // Hidden by default after archiving (AC #1/#2) — reveal it to assert the badge.
    await user.click(screen.getByRole('button', { name: 'Show archived items' }))

    expect(await screen.findByText('Archived')).toBeInTheDocument()
  })

  it('a 400 creating a Room surfaces the backend validation detail instead of a generic message', async () => {
    mockFetchRoutes([
      ...emptyScaffoldRoutes,
      {
        method: 'POST',
        url: '/api/rooms',
        respond: () => jsonResponse({ detail: "A Room named 'Kitchen' already exists." }, 400),
      },
    ])
    const user = userEvent.setup()

    render(<TaggingScaffoldManager />)
    await screen.findByText('No rooms yet.')

    await user.click(screen.getByRole('button', { name: 'Add Room' }))
    await user.type(screen.getByLabelText('Name'), 'Kitchen')
    await user.click(screen.getByRole('button', { name: 'Save' }))

    expect(await screen.findByText("A Room named 'Kitchen' already exists.")).toBeInTheDocument()
  })

  it('a 409 creating a Power Point under a since-archived Room shows the parent-archived error', async () => {
    mockFetchRoutes([
      { method: 'GET', url: '/api/rooms', respond: () => jsonResponse([{ id: 'r1', name: 'Kitchen', archivedAt: null }]) },
      { method: 'GET', url: '/api/power-points', respond: () => jsonResponse([]) },
      { method: 'GET', url: '/api/devices', respond: () => jsonResponse([]) },
      { method: 'POST', url: '/api/power-points', respond: () => jsonResponse(null, 409) },
    ])
    const user = userEvent.setup()

    render(<TaggingScaffoldManager />)
    await user.click(await screen.findByText('Kitchen', { exact: false }))

    await user.click(screen.getByRole('button', { name: 'Add Power Point' }))
    await user.type(screen.getByLabelText('Name'), 'Counter outlet')
    await user.click(screen.getByRole('button', { name: 'Save' }))

    expect(
      await screen.findByText('That parent was archived in the meantime. Please refresh and try again.'),
    ).toBeInTheDocument()
  })

  it('opening "Move to…" on a Power Point renders the Room destination list, current Room tagged/disabled, and moving it re-renders under the new Room', async () => {
    mockFetchRoutes([
      {
        method: 'GET',
        url: '/api/rooms',
        respond: () =>
          jsonResponse([
            { id: 'r1', name: 'Kitchen', archivedAt: null },
            { id: 'r2', name: 'Living room', archivedAt: null },
          ]),
      },
      { method: 'GET', url: '/api/power-points', respond: () => jsonResponse([{ id: 'p1', roomId: 'r1', name: 'Counter outlet', archivedAt: null }]) },
      { method: 'GET', url: '/api/devices', respond: () => jsonResponse([]) },
      {
        method: 'PUT',
        url: '/api/power-points/p1/room',
        respond: (body) => {
          expect(body).toEqual({ roomId: 'r2' })
          return jsonResponse({ id: 'p1', roomId: 'r2', name: 'Counter outlet', archivedAt: null })
        },
      },
    ])
    const user = userEvent.setup()

    render(<TaggingScaffoldManager />)
    const kitchenSummary = await screen.findByText('Kitchen', { exact: false })
    const kitchenDetails = kitchenSummary.closest('details')!
    await user.click(kitchenSummary)

    await user.click(screen.getByRole('button', { name: 'Move to…' }))

    expect(screen.getByRole('button', { name: /Kitchen/ })).toBeDisabled()
    expect(screen.getByText('Current')).toBeInTheDocument()
    const destination = screen.getByRole('button', { name: 'Living room' })
    expect(destination).toBeEnabled()

    await user.click(destination)

    const livingRoomSummary = await screen.findByText('Living room', { exact: false })
    const livingRoomDetails = livingRoomSummary.closest('details')!
    await user.click(livingRoomSummary)

    expect(within(livingRoomDetails).getByText('Counter outlet', { exact: false })).toBeInTheDocument()
    expect(within(kitchenDetails).queryByText('Counter outlet', { exact: false })).not.toBeInTheDocument()
  })

  it('opening "Move to…" on a Device renders the Power Point destination list and moving it issues the expected PUT and re-renders under the new Power Point', async () => {
    mockFetchRoutes([
      { method: 'GET', url: '/api/rooms', respond: () => jsonResponse([{ id: 'r1', name: 'Kitchen', archivedAt: null }]) },
      {
        method: 'GET',
        url: '/api/power-points',
        respond: () =>
          jsonResponse([
            { id: 'p1', roomId: 'r1', name: 'Counter outlet', archivedAt: null },
            { id: 'p2', roomId: 'r1', name: 'Wall outlet', archivedAt: null },
          ]),
      },
      { method: 'GET', url: '/api/devices', respond: () => jsonResponse([{ id: 'd1', powerPointId: 'p1', name: 'Kettle', archivedAt: null }]) },
      {
        method: 'PUT',
        url: '/api/devices/d1/power-point',
        respond: (body) => {
          expect(body).toEqual({ powerPointId: 'p2' })
          return jsonResponse({ id: 'd1', powerPointId: 'p2', name: 'Kettle', archivedAt: null })
        },
      },
    ])
    const user = userEvent.setup()

    render(<TaggingScaffoldManager />)
    await user.click(await screen.findByText('Kitchen', { exact: false }))
    await user.click(await screen.findByText('Counter outlet', { exact: false }))

    const kettleText = await screen.findByText('Kettle', { exact: false })
    const deviceRow = kettleText.closest<HTMLElement>('.flex.items-center.justify-between')!
    await user.click(within(deviceRow).getByRole('button', { name: 'Move to…' }))

    const destination = screen.getByRole('button', { name: 'Kitchen → Wall outlet' })
    await user.click(destination)

    await user.click(await screen.findByText('Wall outlet', { exact: false }))
    expect(await screen.findByText('Kettle', { exact: false })).toBeInTheDocument()
  })

  it('shows noDestinations when the Household has no other non-archived Room to move a Power Point to', async () => {
    mockFetchRoutes([
      { method: 'GET', url: '/api/rooms', respond: () => jsonResponse([{ id: 'r1', name: 'Kitchen', archivedAt: null }]) },
      { method: 'GET', url: '/api/power-points', respond: () => jsonResponse([{ id: 'p1', roomId: 'r1', name: 'Counter outlet', archivedAt: null }]) },
      { method: 'GET', url: '/api/devices', respond: () => jsonResponse([]) },
    ])
    const user = userEvent.setup()

    render(<TaggingScaffoldManager />)
    await user.click(await screen.findByText('Kitchen', { exact: false }))

    await user.click(screen.getByRole('button', { name: 'Move to…' }))

    expect(await screen.findByText('No other option is available to move to.')).toBeInTheDocument()
  })

  it('still offers a valid destination when the Power Point\'s current Room has since been archived', async () => {
    // ArchiveRoom doesn't cascade-archive its Power Points, so a Power Point can end up with an
    // archived current parent. The current-room row then drops out of the non-archived filter
    // entirely, and the destination list must not mistake "current row absent" for "no options".
    mockFetchRoutes([
      {
        method: 'GET',
        url: '/api/rooms',
        respond: () =>
          jsonResponse([
            { id: 'r1', name: 'Kitchen', archivedAt: '2026-01-01T00:00:00Z' },
            { id: 'r2', name: 'Living room', archivedAt: null },
          ]),
      },
      { method: 'GET', url: '/api/power-points', respond: () => jsonResponse([{ id: 'p1', roomId: 'r1', name: 'Counter outlet', archivedAt: null }]) },
      { method: 'GET', url: '/api/devices', respond: () => jsonResponse([]) },
    ])
    const user = userEvent.setup()

    render(<TaggingScaffoldManager />)
    // The archived Kitchen's own row is hidden by default (AC #2) — its live Power Point
    // child is still directly reachable, without expanding a Room row that no longer renders.
    await user.click(await screen.findByText('Counter outlet', { exact: false }))

    await user.click(screen.getByRole('button', { name: 'Move to…' }))

    expect(screen.queryByText('No other option is available to move to.')).not.toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Living room' })).toBeEnabled()
  })

  it('a 409 moving a Power Point into a since-archived Room shows the parent-archived error', async () => {
    mockFetchRoutes([
      {
        method: 'GET',
        url: '/api/rooms',
        respond: () =>
          jsonResponse([
            { id: 'r1', name: 'Kitchen', archivedAt: null },
            { id: 'r2', name: 'Living room', archivedAt: null },
          ]),
      },
      { method: 'GET', url: '/api/power-points', respond: () => jsonResponse([{ id: 'p1', roomId: 'r1', name: 'Counter outlet', archivedAt: null }]) },
      { method: 'GET', url: '/api/devices', respond: () => jsonResponse([]) },
      { method: 'PUT', url: '/api/power-points/p1/room', respond: () => jsonResponse(null, 409) },
    ])
    const user = userEvent.setup()

    render(<TaggingScaffoldManager />)
    await user.click(await screen.findByText('Kitchen', { exact: false }))

    await user.click(screen.getByRole('button', { name: 'Move to…' }))
    await user.click(screen.getByRole('button', { name: 'Living room' }))

    expect(
      await screen.findByText('That parent was archived in the meantime. Please refresh and try again.'),
    ).toBeInTheDocument()
  })

  it('hides archived Rooms from the tree by default with no interaction (AC #1, #6)', async () => {
    mockFetchRoutes([
      {
        method: 'GET',
        url: '/api/rooms',
        respond: () =>
          jsonResponse([
            { id: 'r1', name: 'Kitchen', archivedAt: null },
            { id: 'r2', name: 'Old Pantry', archivedAt: '2026-01-01T00:00:00Z' },
          ]),
      },
      { method: 'GET', url: '/api/power-points', respond: () => jsonResponse([]) },
      { method: 'GET', url: '/api/devices', respond: () => jsonResponse([]) },
    ])

    render(<TaggingScaffoldManager />)

    expect(await screen.findByText('Kitchen', { exact: false })).toBeInTheDocument()
    expect(screen.queryByText('Old Pantry', { exact: false })).not.toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Show archived items' })).toBeInTheDocument()
  })

  it('toggling reveals then re-hides an archived Room that has no live children (AC #2, #3)', async () => {
    mockFetchRoutes([
      {
        method: 'GET',
        url: '/api/rooms',
        respond: () => jsonResponse([{ id: 'r1', name: 'Old Pantry', archivedAt: '2026-01-01T00:00:00Z' }]),
      },
      { method: 'GET', url: '/api/power-points', respond: () => jsonResponse([]) },
      { method: 'GET', url: '/api/devices', respond: () => jsonResponse([]) },
    ])
    const user = userEvent.setup()

    render(<TaggingScaffoldManager />)

    const toggle = await screen.findByRole('button', { name: 'Show archived items' })
    expect(screen.queryByText('Old Pantry', { exact: false })).not.toBeInTheDocument()

    await user.click(toggle)
    expect(await screen.findByText('Old Pantry', { exact: false })).toBeInTheDocument()
    expect(screen.getByText('Archived')).toBeInTheDocument()

    await user.click(screen.getByRole('button', { name: 'Hide archived items' }))
    expect(screen.queryByText('Old Pantry', { exact: false })).not.toBeInTheDocument()
  })

  it('toggling archived visibility issues no extra fetch and leaves Move destinations unchanged (AC #4)', async () => {
    const fetchMock = mockFetchRoutes([
      {
        method: 'GET',
        url: '/api/rooms',
        respond: () =>
          jsonResponse([
            { id: 'r1', name: 'Kitchen', archivedAt: null },
            { id: 'r2', name: 'Old Pantry', archivedAt: '2026-01-01T00:00:00Z' },
          ]),
      },
      { method: 'GET', url: '/api/power-points', respond: () => jsonResponse([{ id: 'p1', roomId: 'r1', name: 'Counter outlet', archivedAt: null }]) },
      { method: 'GET', url: '/api/devices', respond: () => jsonResponse([]) },
    ])
    const user = userEvent.setup()

    render(<TaggingScaffoldManager />)
    await user.click(await screen.findByText('Kitchen', { exact: false }))

    const callCountAfterLoad = fetchMock.mock.calls.length

    await user.click(screen.getByRole('button', { name: 'Move to…' }))
    expect(screen.queryByRole('button', { name: 'Old Pantry' })).not.toBeInTheDocument()
    await user.click(screen.getByRole('button', { name: 'Cancel' }))

    await user.click(screen.getByRole('button', { name: 'Show archived items' }))
    expect(fetchMock.mock.calls.length).toBe(callCountAfterLoad)

    await user.click(screen.getByRole('button', { name: 'Move to…' }))
    expect(screen.queryByRole('button', { name: 'Old Pantry' })).not.toBeInTheDocument()
  })

  it('keeps non-archived children visible when their archived parent is hidden by default (AC #5)', async () => {
    mockFetchRoutes([
      {
        method: 'GET',
        url: '/api/rooms',
        respond: () => jsonResponse([{ id: 'r1', name: 'Old Pantry', archivedAt: '2026-01-01T00:00:00Z' }]),
      },
      {
        method: 'GET',
        url: '/api/power-points',
        respond: () =>
          jsonResponse([
            { id: 'p1', roomId: 'r1', name: 'Counter outlet', archivedAt: null },
            { id: 'p2', roomId: 'r1', name: 'Old outlet', archivedAt: '2026-01-01T00:00:00Z' },
          ]),
      },
      {
        method: 'GET',
        url: '/api/devices',
        respond: () => jsonResponse([{ id: 'd1', powerPointId: 'p2', name: 'Toaster', archivedAt: null }]),
      },
    ])

    render(<TaggingScaffoldManager />)

    // Live Power Point under the archived Room stays reachable; the archived Room's own
    // name/badge is genuinely absent (not just style-hidden).
    expect(await screen.findByText('Counter outlet', { exact: false })).toBeInTheDocument()
    expect(screen.queryByText('Old Pantry', { exact: false })).not.toBeInTheDocument()

    // Live Device under the archived Power Point stays reachable; the archived Power Point's
    // own name/badge is genuinely absent.
    expect(screen.getByText('Toaster', { exact: false })).toBeInTheDocument()
    expect(screen.queryByText('Old outlet', { exact: false })).not.toBeInTheDocument()
  })

  it('keeps a live Device reachable when its Power Point is archived and it is the archived Room\'s only Power Point (AC #5)', async () => {
    mockFetchRoutes([
      {
        method: 'GET',
        url: '/api/rooms',
        respond: () => jsonResponse([{ id: 'r1', name: 'Old Pantry', archivedAt: '2026-01-01T00:00:00Z' }]),
      },
      {
        method: 'GET',
        url: '/api/power-points',
        respond: () => jsonResponse([{ id: 'p1', roomId: 'r1', name: 'Old outlet', archivedAt: '2026-01-01T00:00:00Z' }]),
      },
      {
        method: 'GET',
        url: '/api/devices',
        respond: () => jsonResponse([{ id: 'd1', powerPointId: 'p1', name: 'Toaster', archivedAt: null }]),
      },
    ])

    render(<TaggingScaffoldManager />)

    // Room r1 is archived and its only Power Point (p1) is also archived, but p1 still has a
    // live Device — the Room must not be dropped from the tree entirely just because none of
    // its direct Power Point children are themselves non-archived.
    expect(await screen.findByText('Toaster', { exact: false })).toBeInTheDocument()
    expect(screen.queryByText('Old Pantry', { exact: false })).not.toBeInTheDocument()
    expect(screen.queryByText('Old outlet', { exact: false })).not.toBeInTheDocument()
  })

  it('toggling reveals then re-hides an archived Power Point that has no live children (AC #2, #3)', async () => {
    mockFetchRoutes([
      { method: 'GET', url: '/api/rooms', respond: () => jsonResponse([{ id: 'r1', name: 'Kitchen', archivedAt: null }]) },
      {
        method: 'GET',
        url: '/api/power-points',
        respond: () => jsonResponse([{ id: 'p1', roomId: 'r1', name: 'Old outlet', archivedAt: '2026-01-01T00:00:00Z' }]),
      },
      { method: 'GET', url: '/api/devices', respond: () => jsonResponse([]) },
    ])
    const user = userEvent.setup()

    render(<TaggingScaffoldManager />)
    await user.click(await screen.findByText('Kitchen', { exact: false }))

    const toggle = screen.getByRole('button', { name: 'Show archived items' })
    expect(screen.queryByText('Old outlet', { exact: false })).not.toBeInTheDocument()

    await user.click(toggle)
    expect(await screen.findByText('Old outlet', { exact: false })).toBeInTheDocument()

    await user.click(screen.getByRole('button', { name: 'Hide archived items' }))
    expect(screen.queryByText('Old outlet', { exact: false })).not.toBeInTheDocument()
  })

  it('toggling reveals then re-hides an archived Device (AC #2, #3)', async () => {
    mockFetchRoutes([
      { method: 'GET', url: '/api/rooms', respond: () => jsonResponse([{ id: 'r1', name: 'Kitchen', archivedAt: null }]) },
      {
        method: 'GET',
        url: '/api/power-points',
        respond: () => jsonResponse([{ id: 'p1', roomId: 'r1', name: 'Counter outlet', archivedAt: null }]),
      },
      {
        method: 'GET',
        url: '/api/devices',
        respond: () => jsonResponse([{ id: 'd1', powerPointId: 'p1', name: 'Old toaster', archivedAt: '2026-01-01T00:00:00Z' }]),
      },
    ])
    const user = userEvent.setup()

    render(<TaggingScaffoldManager />)
    await user.click(await screen.findByText('Kitchen', { exact: false }))

    const toggle = screen.getByRole('button', { name: 'Show archived items' })
    expect(screen.queryByText('Old toaster', { exact: false })).not.toBeInTheDocument()

    await user.click(toggle)
    expect(await screen.findByText('Old toaster', { exact: false })).toBeInTheDocument()

    await user.click(screen.getByRole('button', { name: 'Hide archived items' }))
    expect(screen.queryByText('Old toaster', { exact: false })).not.toBeInTheDocument()
  })
})
