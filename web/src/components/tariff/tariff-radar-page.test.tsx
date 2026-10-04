import { render, screen } from '@testing-library/react'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { TariffRadarPage } from './tariff-radar-page'

function jsonResponse(body: object | null, status = 200) {
  return new Response(body === null ? null : JSON.stringify(body), { status })
}

describe('TariffRadarPage', () => {
  afterEach(() => {
    vi.unstubAllGlobals()
  })

  it('mounts the top nav first and the bottom nav last in <main> (Story 10.3)', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn((input: string | URL | Request) => {
        if (String(input).startsWith('/api/tariffs')) {
          return Promise.resolve(jsonResponse({ items: [], totalCount: 0, page: 1, pageSize: 20 }))
        }
        return Promise.resolve(jsonResponse(null))
      }),
    )

    render(
      <TariffRadarPage
        locale="en-US"
        householdCurrency="EUR"
        householdId="11111111-1111-1111-1111-111111111111"
        supportsFederatedLogout={true}
        email={null}
        tariffCheck={null}
        onTariffCheckChanged={() => {}}
        onBack={() => {}}
        onTrendHistoryClick={() => {}}
        onSettingsClick={() => {}}
      />,
    )

    const main = screen.getByRole('main')
    expect(main.firstElementChild).toHaveAttribute('data-slot', 'nav-chrome-top')
    expect(main.lastElementChild).toHaveAttribute('data-slot', 'nav-chrome-bottom')
    expect(main.querySelectorAll(':scope > nav[data-slot]')).toHaveLength(2)
    expect(main.querySelector('#main-content')).toHaveAttribute('data-slot', 'tariff-radar-content')
  })
})
