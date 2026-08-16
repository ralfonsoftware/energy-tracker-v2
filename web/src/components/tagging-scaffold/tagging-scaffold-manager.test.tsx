import { render, screen } from '@testing-library/react'
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
  vi.stubGlobal(
    'fetch',
    vi.fn((input: string | URL | Request, init?: RequestInit) => {
      const url = String(input)
      const method = (init?.method ?? 'GET').toUpperCase()
      const route = routes.find((r) => r.method === method && r.url === url)
      if (!route) {
        throw new Error(`Unmocked fetch: ${method} ${url}`)
      }

      const body = init?.body ? JSON.parse(init.body as string) : undefined
      return Promise.resolve(route.respond(body))
    }),
  )
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
})
