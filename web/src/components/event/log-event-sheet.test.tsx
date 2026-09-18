import { useState } from 'react'
import { render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { LogEventSheet } from './log-event-sheet'
import type { EventDto } from '@/lib/event-api'

function jsonResponse(body: object | null, status = 200) {
  return new Response(body === null ? null : JSON.stringify(body), { status })
}

// LogEventSheet is a controlled component (mirrors LogReadingSheet) — this wrapper owns the
// `open` state the same way App.tsx/DashboardPage do, and renders the saved-Event confirmation
// the way DashboardPage does (the sheet itself deliberately renders none — its trigger lives in
// the fixed-height topbar icon row).
function ControlledLogEventSheet() {
  const [open, setOpen] = useState(false)
  const [saved, setSaved] = useState<EventDto | null>(null)
  return (
    <>
      <LogEventSheet
        trigger={<button>Log event</button>}
        open={open}
        onOpenChange={setOpen}
        onSaved={setSaved}
      />
      {saved && <p role="status">Saved: {saved.description}</p>}
    </>
  )
}

const room = { id: '11111111-1111-1111-1111-111111111111', name: 'Kitchen', archivedAt: null }

interface StubOptions {
  rooms?: object[]
  powerPoints?: object[]
  devices?: object[]
  onEventPost?: (body: unknown) => void
  eventResponse?: { body: object | null; status: number }
  failScaffold?: boolean
}

function stubTaggingScaffoldAndEventFetch({
  rooms = [],
  powerPoints = [],
  devices = [],
  onEventPost,
  eventResponse,
  failScaffold = false,
}: StubOptions = {}) {
  vi.stubGlobal(
    'fetch',
    vi.fn((input: RequestInfo | URL, init?: RequestInit) => {
      const url = typeof input === 'string' ? input : input.toString()
      if (url === '/api/rooms') {
        return failScaffold ? Promise.reject(new Error('offline')) : Promise.resolve(jsonResponse(rooms))
      }
      if (url === '/api/power-points') {
        return failScaffold ? Promise.reject(new Error('offline')) : Promise.resolve(jsonResponse(powerPoints))
      }
      if (url === '/api/devices') {
        return failScaffold ? Promise.reject(new Error('offline')) : Promise.resolve(jsonResponse(devices))
      }
      if (url === '/api/events') {
        const body = init?.body ? JSON.parse(init.body as string) : null
        onEventPost?.(body)
        if (eventResponse) {
          return Promise.resolve(jsonResponse(eventResponse.body, eventResponse.status))
        }
        return Promise.resolve(
          jsonResponse({
            id: 'e1',
            description: body?.description,
            occurredAt: body?.occurredAt,
            taggedEntityType: body?.taggedEntityType ?? null,
            taggedEntityName: body?.taggedEntityType === 'Room' ? 'Kitchen' : null,
          }),
        )
      }
      throw new Error(`Unexpected fetch: ${url}`)
    }),
  )
}

describe('LogEventSheet', () => {
  afterEach(() => {
    vi.unstubAllGlobals()
  })

  it("opens with today's date/time pre-filled and editable", async () => {
    stubTaggingScaffoldAndEventFetch()
    const user = userEvent.setup()
    render(<ControlledLogEventSheet />)

    await user.click(screen.getByRole('button', { name: 'Log event' }))

    const dateTimeInput = await screen.findByLabelText('Date & time')
    const now = new Date()
    const expectedPrefix = `${now.getFullYear()}-${String(now.getMonth() + 1).padStart(2, '0')}-${String(now.getDate()).padStart(2, '0')}T`
    expect((dateTimeInput as HTMLInputElement).value.startsWith(expectedPrefix)).toBe(true)

    await user.clear(dateTimeInput)
    await user.type(dateTimeInput, '2026-08-01T09:15')
    expect(dateTimeInput).toHaveValue('2026-08-01T09:15')
  })

  it('the Save button is disabled while the description is blank', async () => {
    stubTaggingScaffoldAndEventFetch()
    const user = userEvent.setup()
    render(<ControlledLogEventSheet />)

    await user.click(screen.getByRole('button', { name: 'Log event' }))

    expect(await screen.findByRole('button', { name: 'Save event' })).toBeDisabled()

    await user.type(screen.getByLabelText('What happened'), '   ')
    expect(screen.getByRole('button', { name: 'Save event' })).toBeDisabled()
  })

  it('submits without a tag', async () => {
    let posted: unknown = null
    stubTaggingScaffoldAndEventFetch({
      onEventPost: (body) => {
        posted = body
      },
    })
    const user = userEvent.setup()
    render(<ControlledLogEventSheet />)

    await user.click(screen.getByRole('button', { name: 'Log event' }))
    await user.type(await screen.findByLabelText('What happened'), 'away 2 weeks')
    await user.click(screen.getByRole('button', { name: 'Save event' }))

    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument())
    expect(posted).toMatchObject({ description: 'away 2 weeks', taggedEntityType: null, taggedEntityId: null })
    expect(await screen.findByText('Saved: away 2 weeks')).toBeInTheDocument()
  })

  it('submits with a tag', async () => {
    let posted: unknown = null
    stubTaggingScaffoldAndEventFetch({
      rooms: [room],
      onEventPost: (body) => {
        posted = body
      },
    })
    const user = userEvent.setup()
    render(<ControlledLogEventSheet />)

    await user.click(screen.getByRole('button', { name: 'Log event' }))
    await user.type(await screen.findByLabelText('What happened'), 'cooked 2h')
    await user.click(await screen.findByRole('radio', { name: 'Kitchen' }))
    await user.click(screen.getByRole('button', { name: 'Save event' }))

    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument())
    expect(posted).toMatchObject({ description: 'cooked 2h', taggedEntityType: 'Room', taggedEntityId: room.id })
  })

  // AC #3 (backfill): the spec's Task 5.4 asks for a *round-trip*, so this asserts the edited
  // timestamp actually reaches the API, not just that the input holds it.
  it('posts an edited backfill timestamp, not the prefilled now', async () => {
    let posted: { occurredAt?: string } | null = null
    stubTaggingScaffoldAndEventFetch({
      onEventPost: (body) => {
        posted = body as { occurredAt?: string }
      },
    })
    const user = userEvent.setup()
    render(<ControlledLogEventSheet />)

    await user.click(screen.getByRole('button', { name: 'Log event' }))
    const dateTimeInput = await screen.findByLabelText('Date & time')
    await user.clear(dateTimeInput)
    await user.type(dateTimeInput, '2026-08-01T09:15')
    await user.type(screen.getByLabelText('What happened'), 'away 2 weeks')
    await user.click(screen.getByRole('button', { name: 'Save event' }))

    await waitFor(() => expect(posted).not.toBeNull())
    // Compared as an instant: the component sends UTC, so asserting the literal string would make
    // this test depend on the runner's timezone.
    expect(new Date(posted!.occurredAt!).getTime()).toBe(new Date('2026-08-01T09:15').getTime())
  })

  it('renders a localized message from the server errorCode, not the raw English detail', async () => {
    stubTaggingScaffoldAndEventFetch({
      eventResponse: {
        status: 409,
        body: { detail: "Room 'de305d54-75b4-431b-adb2-eb6b9e546014' is archived.", errorCode: 'event.tag_archived' },
      },
    })
    const user = userEvent.setup()
    render(<ControlledLogEventSheet />)

    await user.click(screen.getByRole('button', { name: 'Log event' }))
    await user.type(await screen.findByLabelText('What happened'), 'cooked 2h')
    await user.click(screen.getByRole('button', { name: 'Save event' }))

    expect(await screen.findByRole('alert')).toHaveTextContent(
      'That tag has been archived. Please pick another one.',
    )
    expect(screen.queryByText(/de305d54/)).not.toBeInTheDocument()
  })

  it('shows a session-expired message on a 401 rather than a generic failure', async () => {
    stubTaggingScaffoldAndEventFetch({ eventResponse: { status: 401, body: null } })
    const user = userEvent.setup()
    render(<ControlledLogEventSheet />)

    await user.click(screen.getByRole('button', { name: 'Log event' }))
    await user.type(await screen.findByLabelText('What happened'), 'cooked 2h')
    await user.click(screen.getByRole('button', { name: 'Save event' }))

    expect(await screen.findByRole('alert')).toHaveTextContent(/session has expired/i)
  })

  it('distinguishes a failed tag load from "no tags exist"', async () => {
    stubTaggingScaffoldAndEventFetch({ failScaffold: true })
    const user = userEvent.setup()
    render(<ControlledLogEventSheet />)

    await user.click(screen.getByRole('button', { name: 'Log event' }))

    expect(await screen.findByText(/Tags couldn't be loaded/)).toBeInTheDocument()
    // Still submittable without a tag.
    await user.type(screen.getByLabelText('What happened'), 'away 2 weeks')
    expect(screen.getByRole('button', { name: 'Save event' })).toBeEnabled()
  })

  it('offers a Power Point only while its parent Room is live', async () => {
    const archivedRoom = { id: '22222222-2222-2222-2222-222222222222', name: 'Old Kitchen', archivedAt: '2026-01-01T00:00:00Z' }
    stubTaggingScaffoldAndEventFetch({
      rooms: [room, archivedRoom],
      powerPoints: [
        { id: '33333333-3333-3333-3333-333333333333', roomId: room.id, name: 'Counter', archivedAt: null },
        { id: '44444444-4444-4444-4444-444444444444', roomId: archivedRoom.id, name: 'Orphan', archivedAt: null },
      ],
    })
    const user = userEvent.setup()
    render(<ControlledLogEventSheet />)

    await user.click(screen.getByRole('button', { name: 'Log event' }))

    expect(await screen.findByRole('radio', { name: 'Kitchen → Counter' })).toBeInTheDocument()
    expect(screen.queryByRole('radio', { name: /Orphan/ })).not.toBeInTheDocument()
    expect(screen.queryByRole('radio', { name: 'Old Kitchen' })).not.toBeInTheDocument()
  })
})
