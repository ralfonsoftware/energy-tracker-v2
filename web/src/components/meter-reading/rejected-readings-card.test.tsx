import 'fake-indexeddb/auto'
import { render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import i18next from 'i18next'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { IDBFactory } from 'fake-indexeddb'
import { act } from '@testing-library/react'
import { enqueue, listNeedsAttention, listPending, markRejected } from '@/lib/offline-queue'
import { toDateTimeLocalValue } from '@/lib/datetime-local'
import { RejectedReadingsCard } from './rejected-readings-card'

const householdId = '11111111-1111-1111-1111-111111111111'
const futureReason = "Reading timestamp '2026-11-02T10:00:00+00:00' is too far in the future."
const okBody = { id: 'r1', kwhValue: 4830.25, readingTimestamp: '2026-11-02T10:00:00Z' }

function jsonResponse(body: object, status = 200) {
  return new Response(JSON.stringify(body), { status })
}

function stubFetch(handler: (init: RequestInit) => Promise<Response> | Response) {
  const fetchMock = vi.fn((_url: string, init: RequestInit) => Promise.resolve(handler(init)))
  vi.stubGlobal('fetch', fetchMock)
  return fetchMock
}

function sentBody(fetchMock: ReturnType<typeof stubFetch>, call = 0) {
  return JSON.parse(fetchMock.mock.calls[call][1].body as string) as Record<string, unknown>
}

async function seedRejected(key: string, kwhValue: number, readingTimestamp: string, detail: string | null = futureReason) {
  await enqueue({ householdId, kwhValue, readingTimestamp, idempotencyKey: key })
  await markRejected(key, { status: 400, detail })
}

function renderCard(props: { locale?: string; onResent?: () => void } = {}) {
  return render(<RejectedReadingsCard householdId={householdId} locale={props.locale ?? 'en-US'} onResent={props.onResent ?? (() => {})} />)
}

// Waits until the card's own reads have finished, so "nothing is shown" is asserted after the read, not after a guess.
async function readsSettled() {
  await act(async () => {
    await listNeedsAttention(householdId)
    await listNeedsAttention(householdId)
  })
}

const when = (iso: string, locale = 'en-US') => new Intl.DateTimeFormat(locale, { dateStyle: 'medium', timeStyle: 'short' }).format(new Date(iso))

beforeEach(async () => {
  globalThis.indexedDB = new IDBFactory()
  await i18next.changeLanguage('en-US')
})

afterEach(() => {
  vi.unstubAllGlobals()
  vi.restoreAllMocks()
})

describe('RejectedReadingsCard', () => {
  it('renders nothing when no reading needs attention', async () => {
    await enqueue({ householdId, kwhValue: 1, readingTimestamp: '2026-11-02T10:00:00Z', idempotencyKey: 'pending' })

    const { container } = renderCard()

    await readsSettled()
    expect(container).toBeEmptyDOMElement()
  })

  it('shows one row per entry with value, timestamp, the server reason verbatim, and the fallback when there is none', async () => {
    await seedRejected('a', 4830.25, '2026-11-02T10:00:00Z')
    await seedRejected('b', 4812, '2026-10-27T19:15:00Z', null)

    renderCard()

    expect(await screen.findByRole('heading', { name: '2 readings need your attention' })).toBeInTheDocument()
    const rows = screen.getAllByRole('listitem')
    expect(rows).toHaveLength(2)
    // oldest reading first
    expect(within(rows[0]).getByText(/4,812/)).toBeInTheDocument()
    expect(within(rows[0]).getByText(when('2026-10-27T19:15:00Z'))).toBeInTheDocument()
    expect(within(rows[0]).getByText("The server didn't accept this reading (error 400).")).toBeInTheDocument()
    expect(within(rows[1]).getByText(/4,830.25/)).toBeInTheDocument()
    expect(within(rows[1]).getByText(futureReason)).toBeInTheDocument()
  })

  it('exposes the list as a polite live region', async () => {
    await seedRejected('a', 4830.25, '2026-11-02T10:00:00Z')

    renderCard()

    expect(await screen.findByRole('list')).toHaveAttribute('aria-live', 'polite')
  })

  it('names each action after the reading it acts on', async () => {
    await seedRejected('a', 4830.25, '2026-11-02T10:00:00Z')

    renderCard()

    const ts = when('2026-11-02T10:00:00Z')
    expect(await screen.findByRole('button', { name: `Correct and resend the reading of 4,830.25 kWh from ${ts}` })).toBeInTheDocument()
    expect(screen.getByRole('button', { name: `Discard the reading of 4,830.25 kWh from ${ts}` })).toBeInTheDocument()
  })

  it('appears without a reload when a flush rejects an entry while the card is mounted', async () => {
    renderCard()
    await readsSettled()
    expect(screen.queryByRole('list')).not.toBeInTheDocument()

    await seedRejected('a', 4830.25, '2026-11-02T10:00:00Z')

    expect(await screen.findByText(futureReason)).toBeInTheDocument()
  })

  describe('correct and resend', () => {
    it('opens the dialog pre-filled and resends under the ORIGINAL idempotency key; the row goes on a 200 and onResent fires', async () => {
      const user = userEvent.setup()
      await seedRejected('orig-key', 4830.25, '2026-11-02T10:00:00.000Z')
      const fetchMock = stubFetch(() => jsonResponse(okBody))
      const onResent = vi.fn()
      renderCard({ onResent })

      await user.click(await screen.findByRole('button', { name: /^Correct and resend the reading/ }))

      const dialog = await screen.findByRole('dialog', { name: 'Correct and resend' })
      expect(within(dialog).getByText(futureReason)).toBeInTheDocument()
      expect(within(dialog).getByLabelText('kWh')).toHaveValue(4830.25)
      expect(within(dialog).getByLabelText('Date & time')).toHaveValue(toDateTimeLocalValue(new Date('2026-11-02T10:00:00.000Z')))

      await user.clear(within(dialog).getByLabelText('kWh'))
      await user.type(within(dialog).getByLabelText('kWh'), '4800.5')
      await user.clear(within(dialog).getByLabelText('Date & time'))
      await user.type(within(dialog).getByLabelText('Date & time'), '2026-10-28T09:30')
      await user.click(within(dialog).getByRole('button', { name: 'Resend' }))

      await waitFor(() => expect(onResent).toHaveBeenCalledOnce())
      expect(sentBody(fetchMock)).toEqual({
        kwhValue: 4800.5,
        readingTimestamp: new Date('2026-10-28T09:30').toISOString(),
        idempotencyKey: 'orig-key',
      })
      await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument())
      expect(screen.queryByText(futureReason)).not.toBeInTheDocument()
      expect(await listPending()).toEqual([])
    })

    it('keeps the dialog open with the new reason, and the entry marked, when the server refuses again', async () => {
      const user = userEvent.setup()
      await seedRejected('k', 4830.25, '2026-11-02T10:00:00.000Z')
      stubFetch(() => jsonResponse({ detail: 'Meter value is too large.' }, 400))
      const onResent = vi.fn()
      renderCard({ onResent })

      await user.click(await screen.findByRole('button', { name: /^Correct and resend the reading/ }))
      const dialog = await screen.findByRole('dialog')
      await user.click(within(dialog).getByRole('button', { name: 'Resend' }))

      expect(await within(dialog).findByText('Meter value is too large.')).toBeInTheDocument()
      expect(within(dialog).queryByText(futureReason)).not.toBeInTheDocument()
      expect(screen.getByRole('dialog')).toBeInTheDocument()
      expect(onResent).not.toHaveBeenCalled()
      const [entry] = await listPending()
      expect(entry.rejection).toMatchObject({ status: 400, detail: 'Meter value is too large.' })
      // the card's row (behind the dialog) shows the new reason as well
      await waitFor(() => expect(screen.getAllByText('Meter value is too large.').length).toBeGreaterThanOrEqual(2))
      expect(within(dialog).getByRole('button', { name: 'Resend' })).toBeEnabled()
    })

    it('closes the dialog and says it will sync later when the network fails; the row is gone (pending again)', async () => {
      const user = userEvent.setup()
      await seedRejected('k', 4830.25, '2026-11-02T10:00:00.000Z')
      stubFetch(() => Promise.reject(new TypeError('Failed to fetch')))
      renderCard()

      await user.click(await screen.findByRole('button', { name: /^Correct and resend the reading/ }))
      await user.click(within(await screen.findByRole('dialog')).getByRole('button', { name: 'Resend' }))

      await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument())
      expect(await screen.findByRole('status')).toHaveTextContent("Saved — will sync when you're back online")
      expect(screen.queryByText(futureReason)).not.toBeInTheDocument()
      const [entry] = await listPending()
      expect(entry.rejection).toBeUndefined()
    })

    it('stays open with a "try again" message on a transient answer; the entry is pending with the corrected value, and a retry that succeeds removes it', async () => {
      const user = userEvent.setup()
      await seedRejected('k', 4830.25, '2026-11-02T10:00:00.000Z')
      let answer = () => jsonResponse({ detail: 'boom' }, 503)
      stubFetch(() => answer())
      const onResent = vi.fn()
      renderCard({ onResent })

      await user.click(await screen.findByRole('button', { name: /^Correct and resend the reading/ }))
      const dialog = await screen.findByRole('dialog')
      await user.clear(within(dialog).getByLabelText('kWh'))
      await user.type(within(dialog).getByLabelText('kWh'), '4800.5')
      await user.click(within(dialog).getByRole('button', { name: 'Resend' }))

      expect(await within(dialog).findByText(/Couldn't reach the server just now \(error 503\)/)).toBeInTheDocument()
      expect(screen.getByRole('dialog')).toBeInTheDocument()
      expect(screen.queryByRole('status')).not.toBeInTheDocument()
      expect(onResent).not.toHaveBeenCalled()
      const [entry] = await listPending()
      expect(entry).toMatchObject({ kwhValue: 4800.5, idempotencyKey: 'k' })
      expect(entry.rejection).toBeUndefined()
      expect(within(dialog).getByRole('button', { name: 'Resend' })).toBeEnabled()

      answer = () => jsonResponse(okBody)
      await user.click(within(dialog).getByRole('button', { name: 'Resend' }))

      await waitFor(() => expect(onResent).toHaveBeenCalledOnce())
      await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument())
      expect(await listPending()).toEqual([])
    })

    it('stays open and says so when the device store fails after the server answered', async () => {
      const user = userEvent.setup()
      await seedRejected('k', 4830.25, '2026-11-02T10:00:00.000Z')
      stubFetch(() => {
        vi.spyOn(globalThis.indexedDB, 'open').mockImplementation(() => {
          throw new Error('blocked storage')
        })
        return jsonResponse(okBody)
      })
      renderCard()

      await user.click(await screen.findByRole('button', { name: /^Correct and resend the reading/ }))
      const dialog = await screen.findByRole('dialog')
      await user.click(within(dialog).getByRole('button', { name: 'Resend' }))

      expect(await within(dialog).findByText('Something went wrong while saving on this device. Please try again.')).toBeInTheDocument()
      expect(screen.queryByRole('status')).not.toBeInTheDocument()
      expect(within(dialog).getByRole('button', { name: 'Resend' })).toBeEnabled()
    })

    it('resending one reading leaves the other row untouched', async () => {
      const user = userEvent.setup()
      await seedRejected('one', 4812, '2026-10-27T19:15:00.000Z', 'reason one')
      await seedRejected('two', 4830.25, '2026-11-02T10:00:00.000Z', 'reason two')
      stubFetch(() => jsonResponse(okBody))
      renderCard()

      const buttons = await screen.findAllByRole('button', { name: /^Correct and resend the reading/ })
      await user.click(buttons[0])
      await user.click(within(await screen.findByRole('dialog')).getByRole('button', { name: 'Resend' }))

      await waitFor(() => expect(screen.queryByText('reason one')).not.toBeInTheDocument())
      expect(screen.getByText('reason two')).toBeInTheDocument()
      expect((await listPending()).map((r) => r.idempotencyKey)).toEqual(['two'])
    })

    it('cannot be submitted twice while one is in flight', async () => {
      const user = userEvent.setup()
      await seedRejected('k', 4830.25, '2026-11-02T10:00:00.000Z')
      let finish: (response: Response) => void = () => {}
      const fetchMock = stubFetch(() => new Promise<Response>((resolve) => (finish = resolve)) as unknown as Response)
      renderCard()

      await user.click(await screen.findByRole('button', { name: /^Correct and resend the reading/ }))
      const dialog = await screen.findByRole('dialog')
      await user.click(within(dialog).getByRole('button', { name: 'Resend' }))

      await waitFor(() => expect(within(dialog).getByRole('button', { name: 'Sending…' })).toBeDisabled())
      await user.click(within(dialog).getByRole('button', { name: 'Sending…' }))
      expect(fetchMock).toHaveBeenCalledOnce()
      finish(jsonResponse(okBody))
      await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument())
    })
  })

  describe('discard', () => {
    it('asks first; Keep restores the actions; the second Discard removes the entry and the row', async () => {
      const user = userEvent.setup()
      await seedRejected('k', 4830.25, '2026-11-02T10:00:00.000Z')
      const fetchMock = stubFetch(() => jsonResponse(okBody))
      renderCard()

      await user.click(await screen.findByRole('button', { name: /^Discard the reading/ }))

      expect(screen.getByText("Discard this reading? It can't be recovered.")).toBeInTheDocument()
      expect(await listPending()).toHaveLength(1)
      expect(screen.queryByRole('button', { name: /^Correct and resend the reading/ })).not.toBeInTheDocument()

      await user.click(screen.getByRole('button', { name: 'Keep' }))
      expect(screen.getByRole('button', { name: /^Correct and resend the reading/ })).toBeInTheDocument()
      expect(screen.queryByText("Discard this reading? It can't be recovered.")).not.toBeInTheDocument()

      await user.click(screen.getByRole('button', { name: /^Discard the reading/ }))
      await user.click(screen.getByRole('button', { name: 'Discard' }))

      await waitFor(() => expect(screen.queryByText(futureReason)).not.toBeInTheDocument())
      expect(await listPending()).toEqual([])
      expect(fetchMock).not.toHaveBeenCalled()
    })
  })

  describe('discard step accessibility', () => {
    it('names the confirmation after the reading and moves focus: Keep on open, back to Discard after Keep', async () => {
      const user = userEvent.setup()
      await seedRejected('k', 4830.25, '2026-11-02T10:00:00.000Z')
      renderCard()
      const ts = when('2026-11-02T10:00:00.000Z')

      const discard = await screen.findByRole('button', { name: /^Discard the reading/ })
      await user.click(discard)

      expect(screen.getByRole('group', { name: `Discard the reading of 4,830.25 kWh from ${ts}` })).toBeInTheDocument()
      expect(screen.getByRole('button', { name: 'Keep' })).toHaveFocus()

      await user.click(screen.getByRole('button', { name: 'Keep' }))

      expect(screen.getByRole('button', { name: /^Discard the reading/ })).toHaveFocus()
    })

    it('moves focus to the card heading after a reading is discarded, while others remain', async () => {
      const user = userEvent.setup()
      await seedRejected('one', 4812, '2026-10-27T19:15:00.000Z', 'reason one')
      await seedRejected('two', 4830.25, '2026-11-02T10:00:00.000Z', 'reason two')
      renderCard()

      const [firstDiscard] = await screen.findAllByRole('button', { name: /^Discard the reading/ })
      await user.click(firstDiscard)
      await user.click(screen.getByRole('button', { name: 'Discard' }))

      await waitFor(() => expect(screen.queryByText('reason one')).not.toBeInTheDocument())
      expect(screen.getByRole('heading', { name: '1 reading needs your attention' })).toHaveFocus()
    })

    it('goes back to the two actions, focused on Discard, when the discard cannot be written', async () => {
      const user = userEvent.setup()
      await seedRejected('k', 4830.25, '2026-11-02T10:00:00.000Z')
      renderCard()

      await user.click(await screen.findByRole('button', { name: /^Discard the reading/ }))
      vi.spyOn(globalThis.indexedDB, 'open').mockImplementation(() => {
        throw new Error('blocked storage')
      })
      await user.click(screen.getByRole('button', { name: 'Discard' }))

      await waitFor(() => expect(screen.queryByText("Discard this reading? It can't be recovered.")).not.toBeInTheDocument())
      expect(screen.getByText(futureReason)).toBeInTheDocument()
      expect(screen.getByRole('button', { name: /^Discard the reading/ })).toHaveFocus()
    })
  })

  it('de-DE: German labels and comma decimals; the server reason stays verbatim', async () => {
    await i18next.changeLanguage('de-DE')
    await seedRejected('a', 4830.25, '2026-11-02T10:00:00Z')

    renderCard({ locale: 'de-DE' })

    expect(await screen.findByRole('heading', { name: '1 Zählerstand braucht deine Aufmerksamkeit' })).toBeInTheDocument()
    expect(screen.getByText(/4\.830,25/)).toBeInTheDocument()
    expect(screen.getByText(futureReason)).toBeInTheDocument()
    expect(screen.getByRole('button', { name: new RegExp(`^Zählerstand 4\\.830,25 kWh vom .* korrigieren und erneut senden$`) })).toBeInTheDocument()
    expect(screen.getByRole('button', { name: /verwerfen$/ })).toBeInTheDocument()
  })
})
