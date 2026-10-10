import i18n from '@/i18n'
import { afterEach, describe, expect, it } from 'vitest'
import { translateJobError } from './job-error'

describe('translateJobError', () => {
  afterEach(async () => {
    await i18n.changeLanguage('en-US')
  })

  const fallback = 'Something went wrong. Please try again.'

  it.each([
    ['job-interrupted', 'This was interrupted before it finished, so it was stopped. Please try again.'],
    ['job-retries-exhausted', 'This could not be completed after several attempts. Please try again.'],
    ['upload-missing', 'The uploaded file is no longer available on the server. Please upload it again.'],
  ])('renders the en-US sentence for the code %s', (code, sentence) => {
    expect(translateJobError(i18n.t, code, fallback)).toBe(sentence)
  })

  it('renders the de-DE sentence for a known code when the language is German', async () => {
    await i18n.changeLanguage('de-DE')

    expect(translateJobError(i18n.t, 'job-interrupted', fallback)).toBe(
      'Das wurde unterbrochen, bevor es abgeschlossen war, und daher abgebrochen. Bitte versuche es erneut.',
    )
    expect(translateJobError(i18n.t, 'upload-missing', fallback)).toBe(
      'Die hochgeladene Datei ist auf dem Server nicht mehr verfügbar. Bitte lade sie erneut hoch.',
    )
  })

  it('never shows a raw code to a member', () => {
    expect(translateJobError(i18n.t, 'job-interrupted', fallback)).not.toContain('job-interrupted')
  })

  it('returns an unknown message verbatim (a validation sentence is shown as before)', () => {
    expect(translateJobError(i18n.t, "Couldn't be read as a Meross export", fallback)).toBe("Couldn't be read as a Meross export")
  })

  it('recognises a code by exact equality only', () => {
    expect(translateJobError(i18n.t, 'Job-Interrupted', fallback)).toBe('Job-Interrupted')
    expect(translateJobError(i18n.t, 'job-interrupted ', fallback)).toBe('job-interrupted ')
  })

  it.each([null, undefined, ''])('falls back for %j', (value) => {
    expect(translateJobError(i18n.t, value, fallback)).toBe(fallback)
  })
})
