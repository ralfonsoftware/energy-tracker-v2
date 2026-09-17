// WCAG 2.x relative-luminance / contrast-ratio math (formula: https://www.w3.org/TR/WCAG21/#dfn-relative-luminance).
// Supports the two color formats every --color-* token in index.css actually uses: #rrggbb /
// rgb()/rgba(), and oklch() (used by the shadcn-default tokens, e.g. --muted-foreground). Any
// other format throws rather than silently producing a wrong ratio, so a future token written in
// hsl()/lab()/etc. is a loud parser gap, not a silently-wrong pass.

export interface Rgba {
  r: number
  g: number
  b: number
  a: number
}

const HEX_RE = /^#([0-9a-f]{6})$/i
const RGB_RE = /^rgba?\(\s*([\d.]+)\s*,\s*([\d.]+)\s*,\s*([\d.]+)\s*(?:,\s*([\d.]+)\s*)?\)$/i
const OKLCH_RE = /^oklch\(\s*([\d.]+%?)\s+([\d.]+%?)\s+([\d.]+)\s*(?:\/\s*([\d.]+%?)\s*)?\)$/i

function parsePercentOrNumber(raw: string, hundredPercentValue: number): number {
  return raw.endsWith('%') ? (Number(raw.slice(0, -1)) / 100) * hundredPercentValue : Number(raw)
}

export function parseColor(value: string): Rgba {
  const trimmed = value.trim()

  const hexMatch = HEX_RE.exec(trimmed)
  if (hexMatch) {
    const hex = hexMatch[1]
    return {
      r: parseInt(hex.slice(0, 2), 16),
      g: parseInt(hex.slice(2, 4), 16),
      b: parseInt(hex.slice(4, 6), 16),
      a: 1,
    }
  }

  const rgbMatch = RGB_RE.exec(trimmed)
  if (rgbMatch) {
    return {
      r: Number(rgbMatch[1]),
      g: Number(rgbMatch[2]),
      b: Number(rgbMatch[3]),
      a: rgbMatch[4] === undefined ? 1 : Number(rgbMatch[4]),
    }
  }

  const oklchMatch = OKLCH_RE.exec(trimmed)
  if (oklchMatch) {
    const lightness = parsePercentOrNumber(oklchMatch[1], 1)
    const chroma = parsePercentOrNumber(oklchMatch[2], 0.4) // CSS Color 4: 100% chroma == 0.4
    const hueDegrees = Number(oklchMatch[3])
    const alpha = oklchMatch[4] === undefined ? 1 : parsePercentOrNumber(oklchMatch[4], 1)
    const { r, g, b } = oklchToSrgb8bit(lightness, chroma, hueDegrees)
    return { r, g, b, a: alpha }
  }

  throw new Error(`Unsupported color format for contrast checking: "${value}"`)
}

// OKLCH -> OKLab -> LMS -> linear sRGB -> gamma-encoded sRGB, per the CSS Color 4 spec's own
// reference matrices (https://www.w3.org/TR/css-color-4/#color-conversion-code, Björn Ottosson's
// OKLab). Clamps the final 8-bit channels — out-of-gamut oklch() values are not expected among
// this project's tokens, but clamping keeps a stray one a valid (if imprecise) color rather than
// a NaN/negative that would corrupt every downstream contrast ratio.
function oklchToSrgb8bit(lightness: number, chroma: number, hueDegrees: number): { r: number; g: number; b: number } {
  const hueRadians = (hueDegrees * Math.PI) / 180
  const a = chroma * Math.cos(hueRadians)
  const b = chroma * Math.sin(hueRadians)

  const lNonlinear = lightness + 0.3963377774 * a + 0.2158037573 * b
  const mNonlinear = lightness - 0.1055613458 * a - 0.0638541728 * b
  const sNonlinear = lightness - 0.0894841775 * a - 1.2914855480 * b

  const l = lNonlinear ** 3
  const m = mNonlinear ** 3
  const s = sNonlinear ** 3

  const linear = {
    r: +4.0767416621 * l - 3.3077115913 * m + 0.2309699292 * s,
    g: -1.2684380046 * l + 2.6097574011 * m - 0.3413193965 * s,
    b: -0.0041960863 * l - 0.7034186147 * m + 1.7076147010 * s,
  }

  const gammaEncode = (c: number) => (c <= 0.0031308 ? 12.92 * c : 1.055 * Math.abs(c) ** (1 / 2.4) * Math.sign(c) - 0.055)
  const clamp8bit = (c: number) => Math.round(Math.min(1, Math.max(0, gammaEncode(c))) * 255)

  return { r: clamp8bit(linear.r), g: clamp8bit(linear.g), b: clamp8bit(linear.b) }
}

// Alpha-composites `fg` over an opaque `bg` ("source-over"), returning the resulting opaque color.
export function compositeOver(fg: Rgba, bg: Rgba): Rgba {
  if (bg.a !== 1) {
    throw new Error('compositeOver requires an opaque backdrop — composite it down to a=1 first')
  }
  return {
    r: fg.a * fg.r + (1 - fg.a) * bg.r,
    g: fg.a * fg.g + (1 - fg.a) * bg.g,
    b: fg.a * fg.b + (1 - fg.a) * bg.b,
    a: 1,
  }
}

function channelLuminance(channel8bit: number): number {
  const c = channel8bit / 255
  return c <= 0.03928 ? c / 12.92 : ((c + 0.055) / 1.055) ** 2.4
}

export function relativeLuminance({ r, g, b }: Rgba): number {
  return 0.2126 * channelLuminance(r) + 0.7152 * channelLuminance(g) + 0.0722 * channelLuminance(b)
}

export function contrastRatio(a: Rgba, b: Rgba): number {
  const l1 = relativeLuminance(a)
  const l2 = relativeLuminance(b)
  const [lighter, darker] = l1 >= l2 ? [l1, l2] : [l2, l1]
  return (lighter + 0.05) / (darker + 0.05)
}
