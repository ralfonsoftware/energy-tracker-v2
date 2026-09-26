import { describe, expect, it } from 'vitest'
import deDE from './de-DE/translation.json'
import enUS from './en-US/translation.json'

// WCAG 2.5.3 (Label in Name): a control's visible label must be contained within its accessible
// name. dashboard-page.tsx's header-icon buttons keep their full-sentence `entryPointLabel` as
// aria-label/title while showing `shortLabel` as the visible text at the wide breakpoint — this
// guards that relationship so a future copy edit to either key can't silently break compliance.
describe('translation short-label / entry-point-label WCAG 2.5.3 consistency', () => {
  it.each([
    { locale: 'en-US', translations: enUS },
    { locale: 'de-DE', translations: deDE },
  ])('$locale: event.shortLabel is contained in event.entryPointLabel', ({ translations }) => {
    expect(translations.event.entryPointLabel.toLowerCase()).toContain(translations.event.shortLabel.toLowerCase())
  })

  it.each([
    { locale: 'en-US', translations: enUS },
    { locale: 'de-DE', translations: deDE },
  ])('$locale: smartPlugImport.shortLabel is contained in smartPlugImport.entryPointLabel', ({ translations }) => {
    expect(translations.smartPlugImport.entryPointLabel.toLowerCase()).toContain(translations.smartPlugImport.shortLabel.toLowerCase())
  })
})
