import { describe, expect, it } from 'vitest'
import { compositeOver, contrastRatio, parseColor, relativeLuminance } from './contrast'

describe('parseColor', () => {
  it('parses #rrggbb hex as opaque', () => {
    expect(parseColor('#FFFFFF')).toEqual({ r: 255, g: 255, b: 255, a: 1 })
    expect(parseColor('#000000')).toEqual({ r: 0, g: 0, b: 0, a: 1 })
    expect(parseColor('#1E7A61')).toEqual({ r: 30, g: 122, b: 97, a: 1 })
  })

  it('parses rgb()/rgba()', () => {
    expect(parseColor('rgb(255, 0, 128)')).toEqual({ r: 255, g: 0, b: 128, a: 1 })
    expect(parseColor('rgba(107, 134, 86, 0.14)')).toEqual({ r: 107, g: 134, b: 86, a: 0.14 })
  })

  it('parses achromatic oklch() (chroma 0) as a neutral gray', () => {
    const black = parseColor('oklch(0 0 0)')
    expect(black).toEqual({ r: 0, g: 0, b: 0, a: 1 })

    const white = parseColor('oklch(1 0 0)')
    expect(white.r).toBeGreaterThanOrEqual(254)
    expect(white.g).toBeGreaterThanOrEqual(254)
    expect(white.b).toBeGreaterThanOrEqual(254)

    const midGray = parseColor('oklch(0.556 0 0)')
    expect(midGray.r).toBe(midGray.g)
    expect(midGray.g).toBe(midGray.b)
    expect(midGray.a).toBe(1)
  })

  it('parses oklch() alpha, including percentage alpha', () => {
    expect(parseColor('oklch(0.708 0 0 / 10%)').a).toBeCloseTo(0.1, 5)
    expect(parseColor('oklch(0.708 0 0 / 0.5)').a).toBe(0.5)
  })

  it('throws on an unsupported color format', () => {
    expect(() => parseColor('hsl(120deg 50% 50%)')).toThrow(/Unsupported color format/)
  })
})

describe('compositeOver', () => {
  it('returns the foreground unchanged when fully opaque', () => {
    const fg = { r: 10, g: 20, b: 30, a: 1 }
    const bg = { r: 255, g: 255, b: 255, a: 1 }
    expect(compositeOver(fg, bg)).toEqual({ r: 10, g: 20, b: 30, a: 1 })
  })

  it('returns the backdrop unchanged when fully transparent', () => {
    const fg = { r: 10, g: 20, b: 30, a: 0 }
    const bg = { r: 255, g: 255, b: 255, a: 1 }
    expect(compositeOver(fg, bg)).toEqual({ r: 255, g: 255, b: 255, a: 1 })
  })

  it('blends 50% black over white to mid-gray', () => {
    const fg = { r: 0, g: 0, b: 0, a: 0.5 }
    const bg = { r: 255, g: 255, b: 255, a: 1 }
    const result = compositeOver(fg, bg)
    expect(result).toEqual({ r: 127.5, g: 127.5, b: 127.5, a: 1 })
  })

  it('throws when the backdrop is not opaque', () => {
    expect(() => compositeOver({ r: 0, g: 0, b: 0, a: 0.5 }, { r: 255, g: 255, b: 255, a: 0.9 })).toThrow(
      /requires an opaque backdrop/
    )
  })
})

describe('relativeLuminance / contrastRatio', () => {
  it('gives black relative luminance 0 and white relative luminance 1', () => {
    expect(relativeLuminance({ r: 0, g: 0, b: 0, a: 1 })).toBe(0)
    expect(relativeLuminance({ r: 255, g: 255, b: 255, a: 1 })).toBeCloseTo(1, 5)
  })

  it('gives black-on-white (and white-on-black) the canonical 21:1 ratio', () => {
    const black = { r: 0, g: 0, b: 0, a: 1 }
    const white = { r: 255, g: 255, b: 255, a: 1 }
    expect(contrastRatio(black, white)).toBeCloseTo(21, 1)
    expect(contrastRatio(white, black)).toBeCloseTo(21, 1)
  })

  it('gives identical colors a 1:1 ratio', () => {
    const color = { r: 107, g: 134, b: 86, a: 1 }
    expect(contrastRatio(color, color)).toBeCloseTo(1, 5)
  })
})
