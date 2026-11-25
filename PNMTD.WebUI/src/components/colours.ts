/**
 * Colours as #rrggbb (docs/responsibilities-extensions-plan.md §4): the check, the text that reads on a colour, and the swatches
 * responsibilities pick from. The availability plan's types and people use the same check and contrast.
 */

export const isColour = (value: string | null | undefined): value is string => !!value && /^#[0-9a-f]{6}$/i.test(value)

/** WCAG's relative luminance of #rrggbb. */
export function luminance(hex: string): number {
  const channel = (i: number) => {
    const c = Number.parseInt(hex.slice(i, i + 2), 16) / 255
    return c <= 0.03928 ? c / 12.92 : ((c + 0.055) / 1.055) ** 2.4
  }
  return 0.2126 * channel(1) + 0.7152 * channel(3) + 0.0722 * channel(5)
}

/** WCAG's contrast ratio of two colours, 1 to 21. */
export function contrast(a: string, b: string): number {
  const [hi, lo] = [luminance(a), luminance(b)].sort((x, y) => y - x)
  return (hi + 0.05) / (lo + 0.05)
}

/** Black or white, whichever reads better on the colour (the higher contrast); black when it isn't a colour. */
export function textOn(background: string | null | undefined): '#000000' | '#ffffff' {
  if (!isColour(background)) return '#000000'
  const l = luminance(background)
  return (l + 0.05) / 0.05 >= 1.05 / (l + 0.05) ? '#000000' : '#ffffff'
}

/** A code shown on its colour (a type's, a person's): the background and a text colour that reads on it. */
export function chipStyle(colour: string | null | undefined): Record<string, string> {
  return isColour(colour) ? { background: colour, color: textOn(colour) } : {}
}

/** The twelve swatches (§4.3), chosen to read on both themes; each dot also has an outline in the border colour. */
export const palette: readonly { name: string; hex: string }[] = [
  { name: 'Red', hex: '#d94f4f' },
  { name: 'Orange', hex: '#e8833a' },
  { name: 'Amber', hex: '#d9a400' },
  { name: 'Lime', hex: '#7cb342' },
  { name: 'Green', hex: '#2e9d5b' },
  { name: 'Teal', hex: '#1fa3a3' },
  { name: 'Blue', hex: '#2f8fd8' },
  { name: 'Indigo', hex: '#4f5bd5' },
  { name: 'Violet', hex: '#8e5cd9' },
  { name: 'Pink', hex: '#d0559b' },
  { name: 'Brown', hex: '#8d6e63' },
  { name: 'Slate', hex: '#78909c' },
]

/** A swatch's name, or the hex itself for a custom colour: for labels and screen readers. */
export function colourName(hex: string | null | undefined): string {
  if (!isColour(hex)) return 'No colour'
  return palette.find((p) => p.hex === hex.toLowerCase())?.name ?? hex.toLowerCase()
}

/** The first swatch nobody uses yet, for a new responsibility (decision 3); the first one when all are taken. */
export function firstUnused(used: readonly (string | null | undefined)[]): string {
  const taken = new Set(used.filter(isColour).map((c) => c.toLowerCase()))
  return (palette.find((p) => !taken.has(p.hex)) ?? palette[0]!).hex
}
