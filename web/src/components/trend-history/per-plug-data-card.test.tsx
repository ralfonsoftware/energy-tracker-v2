import { render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { PerPlugDataCard } from './per-plug-data-card'
import type { RoomMeasuredDataDto } from '@/lib/smart-plug-reading-api'

function jsonResponse(body: object | null, status = 200) {
  return new Response(body === null ? null : JSON.stringify(body), { status })
}

const rooms: RoomMeasuredDataDto[] = [
  {
    roomName: 'Living Room',
    totalKwh: 60,
    powerPoints: [
      {
        powerPointName: 'TV Power Point',
        totalKwh: 60,
        devices: [
          { deviceName: 'Smart TV', totalKwh: 38 },
          { deviceName: 'Games Console', totalKwh: 22 },
        ],
      },
    ],
  },
]

describe('PerPlugDataCard', () => {
  afterEach(() => {
    vi.unstubAllGlobals()
  })

  it('renders the heading and the caveat text always, even before data loads', async () => {
    vi.stubGlobal('fetch', vi.fn(() => Promise.resolve(jsonResponse([]))))

    render(<PerPlugDataCard locale="en-US" />)

    expect(screen.getByText('Room → Power Point → Device')).toBeInTheDocument()
    expect(
      screen.getByText("Measured context, not a reconciled attribution of your Main Meter total — these figures won't sum to it."),
    ).toBeInTheDocument()
    await screen.findByText('No Smart Plug data imported yet.')
  })

  it('renders a loading message while the fetch is in flight', async () => {
    let resolveFetch: (value: Response) => void = () => {}
    vi.stubGlobal(
      'fetch',
      vi.fn(() => new Promise<Response>((resolve) => (resolveFetch = resolve))),
    )

    render(<PerPlugDataCard locale="en-US" />)

    expect(screen.getByText('Loading your Smart Plug data…')).toBeInTheDocument()

    resolveFetch(jsonResponse([]))
    await screen.findByText('No Smart Plug data imported yet.')
    expect(screen.queryByText('Loading your Smart Plug data…')).not.toBeInTheDocument()
  })

  it('renders the empty state when no Rooms are returned', async () => {
    vi.stubGlobal('fetch', vi.fn(() => Promise.resolve(jsonResponse([]))))

    render(<PerPlugDataCard locale="en-US" />)

    expect(await screen.findByText('No Smart Plug data imported yet.')).toBeInTheDocument()
  })

  it('renders Rooms collapsed by default, with Power Points and Devices only visible after expanding', async () => {
    const user = userEvent.setup()
    vi.stubGlobal('fetch', vi.fn(() => Promise.resolve(jsonResponse(rooms))))

    render(<PerPlugDataCard locale="en-US" />)

    const roomSummary = await screen.findByText('Living Room')
    const roomDetails = roomSummary.closest('details')
    expect(roomDetails).not.toHaveAttribute('open')
    expect(screen.getByText('TV Power Point')).not.toBeVisible()
    expect(screen.getByText('Smart TV')).not.toBeVisible()

    await user.click(roomSummary)
    expect(roomDetails).toHaveAttribute('open')
    const powerPointSummary = screen.getByText('TV Power Point')
    expect(powerPointSummary).toBeVisible()
    const powerPointDetails = powerPointSummary.closest('details')
    expect(powerPointDetails).not.toHaveAttribute('open')
    expect(screen.getByText('Smart TV')).not.toBeVisible()

    await user.click(powerPointSummary)
    expect(powerPointDetails).toHaveAttribute('open')
    expect(screen.getByText('Smart TV')).toBeVisible()
    expect(screen.getByText('Games Console')).toBeVisible()
  })

  it('shows each Room/Power Point/Device kWh total', async () => {
    const user = userEvent.setup()
    vi.stubGlobal('fetch', vi.fn(() => Promise.resolve(jsonResponse(rooms))))

    render(<PerPlugDataCard locale="en-US" />)

    await screen.findByText('Living Room')
    // Room total (60 kWh) and its sole Power Point's total (also 60 kWh) are both present in the
    // DOM even before expanding (details children render, just visually hidden) — assert the
    // count rather than a single match.
    expect(screen.getAllByText('60 kWh')).toHaveLength(2)

    await user.click(screen.getByText('Living Room'))
    await user.click(screen.getByText('TV Power Point'))

    expect(screen.getByText('38 kWh')).toBeInTheDocument()
    expect(screen.getByText('22 kWh')).toBeInTheDocument()
  })

  it('formats kWh totals using the given household locale, not the browser default', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn(() =>
        Promise.resolve(
          jsonResponse([
            {
              roomName: 'Wohnzimmer',
              totalKwh: 1234.5,
              powerPoints: [
                {
                  powerPointName: 'Steckdose',
                  totalKwh: 1234.5,
                  devices: [{ deviceName: 'Fernseher', totalKwh: 1234.5 }],
                },
              ],
            },
          ]),
        ),
      ),
    )

    render(<PerPlugDataCard locale="de-DE" />)

    await screen.findByText('Wohnzimmer')
    // de-DE groups with '.' and uses ',' for the decimal separator. Room, Power Point, and Device
    // all share the same total here (one of each), so all three renders are present at once even
    // before expanding (details children render, just visually hidden).
    expect(screen.getAllByText('1.234,5 kWh')).toHaveLength(3)
  })

  it('renders a load-error state on a fetch failure, not the empty state', async () => {
    vi.stubGlobal('fetch', vi.fn(() => Promise.resolve(new Response(JSON.stringify({ detail: 'boom' }), { status: 500 }))))

    render(<PerPlugDataCard locale="en-US" />)

    expect(await screen.findByText("Couldn't load your Smart Plug data — try again.")).toBeInTheDocument()
    expect(screen.queryByText('No Smart Plug data imported yet.')).not.toBeInTheDocument()
  })
})
