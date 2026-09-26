import { render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { EventsCard } from './events-card'
import type { EventDto, EventHistoryPageDto } from '@/lib/event-api'

function jsonResponse(body: object | null, status = 200) {
  return new Response(body === null ? null : JSON.stringify(body), { status })
}

function eventItem(overrides: Partial<EventDto> = {}): EventDto {
  return {
    id: '11111111-1111-1111-1111-111111111111',
    description: 'cooked 2h',
    occurredAt: '2026-08-15T14:32:00+00:00',
    taggedEntityType: null,
    taggedEntityName: null,
    correlationDirection: null,
    ...overrides,
  }
}

function page(overrides: Partial<EventHistoryPageDto> = {}): EventHistoryPageDto {
  return {
    items: [eventItem()],
    totalCount: 1,
    page: 1,
    pageSize: 20,
    ...overrides,
  }
}

async function openDisclosure(user: ReturnType<typeof userEvent.setup>) {
  await user.click(screen.getByText(/logged/))
}

describe('EventsCard', () => {
  afterEach(() => {
    vi.unstubAllGlobals()
  })

  it('shows a live count summary in the collapsed disclosure', async () => {
    vi.stubGlobal('fetch', vi.fn(() => Promise.resolve(jsonResponse(page({ totalCount: 7 })))))

    render(<EventsCard locale="en-US" />)

    expect(await screen.findByText('Events — 7 logged')).toBeInTheDocument()
  })

  it('renders a tagged Event with its taggedEntityName as plain text', async () => {
    const user = userEvent.setup()
    vi.stubGlobal(
      'fetch',
      vi.fn(() =>
        Promise.resolve(
          jsonResponse(page({ items: [eventItem({ taggedEntityType: 'Room', taggedEntityName: 'Kitchen' })] })),
        ),
      ),
    )

    render(<EventsCard locale="en-US" />)
    await openDisclosure(user)

    expect(await screen.findByText('cooked 2h')).toBeInTheDocument()
    expect(screen.getByText('Kitchen')).toBeInTheDocument()
  })

  it('renders an untagged Event with no empty tag affordance (AC #4)', async () => {
    const user = userEvent.setup()
    vi.stubGlobal('fetch', vi.fn(() => Promise.resolve(jsonResponse(page({ items: [eventItem()] })))))

    render(<EventsCard locale="en-US" />)
    await openDisclosure(user)

    const descriptionCell = await screen.findByText('cooked 2h')
    const row = descriptionCell.closest('tr')
    // Only the description text node itself — no second (empty/placeholder) line for the tag.
    expect(row?.querySelectorAll('span')).toHaveLength(1)
  })

  // Story 6.3 (AC #1, #3, #7): rendered inline in the same row, never a separate view; a fixed,
  // translated string, never raw AI output; absent renders cleanly with no extra markup (UX-DR14).
  it('renders the Bump correlation inline with the Event', async () => {
    const user = userEvent.setup()
    vi.stubGlobal(
      'fetch',
      vi.fn(() => Promise.resolve(jsonResponse(page({ items: [eventItem({ correlationDirection: 'Bump' })] })))),
    )

    render(<EventsCard locale="en-US" />)
    await openDisclosure(user)

    expect(await screen.findByText('Roughly matches the bump seen.')).toBeInTheDocument()
  })

  it('renders the Dip correlation inline with the Event', async () => {
    const user = userEvent.setup()
    vi.stubGlobal(
      'fetch',
      vi.fn(() => Promise.resolve(jsonResponse(page({ items: [eventItem({ correlationDirection: 'Dip' })] })))),
    )

    render(<EventsCard locale="en-US" />)
    await openDisclosure(user)

    expect(await screen.findByText('Roughly matches the dip seen.')).toBeInTheDocument()
  })

  it('renders no correlation markup at all when correlationDirection is null', async () => {
    const user = userEvent.setup()
    vi.stubGlobal('fetch', vi.fn(() => Promise.resolve(jsonResponse(page({ items: [eventItem()] })))))

    render(<EventsCard locale="en-US" />)
    await openDisclosure(user)

    const descriptionCell = await screen.findByText('cooked 2h')
    const row = descriptionCell.closest('tr')
    expect(row?.querySelectorAll('span')).toHaveLength(1)
    expect(screen.queryByText(/roughly matches/i)).not.toBeInTheDocument()
  })

  it('renders the empty state when totalCount is 0', async () => {
    const user = userEvent.setup()
    vi.stubGlobal('fetch', vi.fn(() => Promise.resolve(jsonResponse(page({ items: [], totalCount: 0 })))))

    render(<EventsCard locale="en-US" />)
    await openDisclosure(user)

    expect(await screen.findByText('No Events logged yet.')).toBeInTheDocument()
  })

  it('tightens the Timestamp column to its content width, for consistency with MeterReadingsCard (AC #2)', async () => {
    const user = userEvent.setup()
    vi.stubGlobal('fetch', vi.fn(() => Promise.resolve(jsonResponse(page()))))

    render(<EventsCard locale="en-US" />)
    await openDisclosure(user)

    await screen.findByText('cooked 2h')
    expect(screen.getByRole('columnheader', { name: 'Date & time' })).toHaveClass('w-px')
  })

  it('renders an error state when the fetch fails', async () => {
    const user = userEvent.setup()
    vi.stubGlobal('fetch', vi.fn(() => Promise.resolve(new Response(JSON.stringify({ detail: 'boom' }), { status: 500 }))))

    render(<EventsCard locale="en-US" />)
    await openDisclosure(user)

    expect(await screen.findByText('Something went wrong. Please try again.')).toBeInTheDocument()
  })

  // AD-10 guard: an Event whose tagged entity was archived after logging must still render its
  // original snapshot, and must never trigger a lookup against the live scaffold endpoints. This
  // asserts on the fetch stub, not just the DOM — a DOM-only assertion would still pass if someone
  // added a resolve-then-render path that happened to return the same name.
  it('renders the tag of an Event whose entity was archived, without ever calling the scaffold endpoints', async () => {
    const user = userEvent.setup()
    const fetchMock = vi.fn((input: string | URL | Request) => {
      const url = String(input)
      if (url.startsWith('/api/events')) {
        return Promise.resolve(
          jsonResponse(page({ items: [eventItem({ taggedEntityType: 'Room', taggedEntityName: 'Kitchen' })] })),
        )
      }
      return Promise.resolve(jsonResponse(null, 500))
    })
    vi.stubGlobal('fetch', fetchMock)

    render(<EventsCard locale="en-US" />)
    await openDisclosure(user)

    expect(await screen.findByText('Kitchen')).toBeInTheDocument()
    const calledUrls = fetchMock.mock.calls.map(([input]) => String(input))
    expect(calledUrls.some((url) => url.startsWith('/api/rooms'))).toBe(false)
    expect(calledUrls.some((url) => url.startsWith('/api/power-points'))).toBe(false)
    expect(calledUrls.some((url) => url.startsWith('/api/devices'))).toBe(false)
  })

  it('disables Previous on the first page and Next on the last page, and paginates on click', async () => {
    const user = userEvent.setup()
    const fetchMock = vi.fn((input: string | URL | Request) => {
      const url = String(input)
      if (url.includes('page=2')) {
        return Promise.resolve(
          jsonResponse(page({ items: [eventItem({ id: 'e2', description: 'second' })], totalCount: 40, page: 2, pageSize: 20 })),
        )
      }
      return Promise.resolve(
        jsonResponse(page({ items: [eventItem({ id: 'e1', description: 'first' })], totalCount: 40, page: 1, pageSize: 20 })),
      )
    })
    vi.stubGlobal('fetch', fetchMock)

    render(<EventsCard locale="en-US" />)
    await openDisclosure(user)

    await screen.findByText('first')
    expect(screen.getByRole('button', { name: 'Previous' })).toBeDisabled()
    expect(screen.getByRole('button', { name: 'Next' })).not.toBeDisabled()

    await user.click(screen.getByRole('button', { name: 'Next' }))

    await screen.findByText('second')
    expect(screen.getByText('Page 2 of 2')).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Next' })).toBeDisabled()
    expect(screen.getByRole('button', { name: 'Previous' })).not.toBeDisabled()
  })
})
