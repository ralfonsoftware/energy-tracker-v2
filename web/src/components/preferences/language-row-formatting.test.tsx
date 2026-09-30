import { useState } from 'react'
import { render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import i18next from 'i18next'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { StatusCard } from '@/components/dashboard/status-card'
import { HouseholdLocaleContext } from '@/lib/household-locale-context'
import { LanguageRow } from './language-row'

// Mirrors App.tsx's seam: context locale state + the changeLanguage effect, feeding a real
// locale-consuming surface through the `locale` prop.
function Harness() {
  const [locale, setLocale] = useState('en-US')
  return (
    <HouseholdLocaleContext.Provider value={{ locale, setLocale: (l) => { setLocale(l); void i18next.changeLanguage(l) } }}>
      <LanguageRow householdId="h-1" />
      <StatusCard
        status={{ status: 'trending', paceToDateKwh: 6821, baselineToDateKwh: 2000, isLowConfidence: false }}
        loading={false}
        locale={locale}
        playEntranceAnimation={false}
      />
    </HouseholdLocaleContext.Provider>
  )
}

describe('Language switch formatting (Story 8.11, AC #2 "together")', () => {
  afterEach(async () => {
    vi.unstubAllGlobals()
    await i18next.changeLanguage('en-US')
  })

  it('flips number formatting and UI copy together without remounting', async () => {
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(new Response('{}', { status: 200 })))
    const user = userEvent.setup()
    render(<Harness />)
    expect(screen.getByText(/4,821/)).toBeInTheDocument()

    await user.click(screen.getByRole('radio', { name: 'Deutsch' }))

    await waitFor(() => expect(screen.getByText(/4\.821/)).toBeInTheDocument())
    expect(screen.queryByText(/4,821/)).not.toBeInTheDocument()
    expect(screen.getByRole('radiogroup', { name: 'Sprache' })).toBeInTheDocument()
  })
})
