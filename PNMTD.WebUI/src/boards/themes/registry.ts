/**
 * The themes of a board (docs/boards-plan.md §9.2): panag's fifteen, in its picker's order, with PNMT's blue in Default and Dark mode.
 * Each is a block of themes.css scoped on [data-board-theme]. The server keeps the same ids (BoardThemes.Ids), and a test compares them.
 */
export interface BoardTheme {
  id: string
  name: string
  line: string
  dark: boolean
}

export const themes: readonly BoardTheme[] = [
  { id: 'default', name: 'Default', line: 'PNMT blue on light grey', dark: false },
  { id: 'dark', name: 'Dark mode', line: 'Dark surfaces, easy on the eyes at night', dark: true },
  {
    id: 'high-contrast',
    name: 'High contrast',
    line: 'Black on white, strong borders and focus outlines (WCAG AAA)',
    dark: false,
  },
  { id: 'high-contrast-dark', name: 'High contrast dark', line: 'White and yellow on black (WCAG AAA)', dark: true },
  { id: 'slay', name: 'Slay', line: 'Violet glam with a pride stripe ✨', dark: false },
  { id: 'switzerland', name: 'Switzerland', line: 'Swiss red and white, Helvetica, flat and strict', dark: false },
  { id: 'terminal', name: 'Terminal', line: 'Green on black, monospace, $ prompts', dark: true },
  { id: 'brasil', name: 'Brasil', line: 'Verde, amarelo e azul – green, yellow and blue', dark: false },
  { id: 'atomic-age', name: 'Atomic age', line: '1950s retro-futurism: teal, mustard and starbursts', dark: false },
  { id: 'wtf', name: 'WTF', line: 'Comic Sans, clashing neon, wonky cards. Why? Why not.', dark: false },
  { id: 'suse', name: 'SUSE Linux', line: 'Pine and jungle green on fog grey', dark: false },
  { id: 'win95', name: 'Windows 95', line: 'Teal desktop, grey bevelled windows, navy title bars', dark: false },
  { id: 'winxp', name: 'Windows XP', line: 'Blue title bars, green start button, beige windows', dark: false },
  { id: 'ubuntu', name: 'Ubuntu', line: 'Aubergine and orange', dark: false },
  { id: 'amiga', name: 'Amiga', line: 'Workbench blue, white and orange – failures as Guru Meditation', dark: true },
]

export const themeName = (id: string) => themes.find((t) => t.id === id)?.name ?? id

/**
 * Each theme's status colours (§9.4), as themes.css declares them: the fill and the text on it. The tests check that a theme keeps each
 * status in its hue family and its text readable. Amiga's failure is a Guru Meditation: red text and frame on black.
 */
export interface StatusColours {
  ok: [fill: string, on: string]
  warn: [fill: string, on: string]
  fail: [fill: string, on: string]
}

const bootstrap: StatusColours = {
  ok: ['#198754', '#ffffff'],
  warn: ['#ffc107', '#000000'],
  fail: ['#dc3545', '#ffffff'],
}

export const statusColours: Record<string, StatusColours> = {
  default: bootstrap,
  dark: bootstrap,
  'high-contrast': { ok: ['#0b5a1c', '#ffffff'], warn: ['#ffc107', '#000000'], fail: ['#a30000', '#ffffff'] },
  'high-contrast-dark': { ok: ['#7dff8a', '#000000'], warn: ['#ffd60a', '#000000'], fail: ['#ff8a8a', '#000000'] },
  slay: bootstrap,
  switzerland: { ...bootstrap, fail: ['#c80000', '#ffffff'] },
  terminal: { ok: ['#33ff33', '#000000'], warn: ['#ffcc00', '#000000'], fail: ['#ff5555', '#000000'] },
  brasil: { ok: ['#00702b', '#ffffff'], warn: ['#ffdf00', '#000000'], fail: ['#c8102e', '#ffffff'] },
  'atomic-age': { ok: ['#4f7a28', '#ffffff'], warn: ['#e8a33d', '#000000'], fail: ['#a8361a', '#ffffff'] },
  wtf: { ok: ['#007a33', '#ffffff'], warn: ['#ffe600', '#000000'], fail: ['#d00000', '#ffffff'] },
  suse: { ...bootstrap, ok: ['#0a6640', '#ffffff'] },
  win95: { ok: ['#006400', '#ffffff'], warn: ['#ffc107', '#000000'], fail: ['#800000', '#ffffff'] },
  winxp: { ...bootstrap, fail: ['#a00000', '#ffffff'] },
  ubuntu: bootstrap,
  amiga: { ok: ['#a6f5b0', '#000000'], warn: ['#ffd27a', '#000000'], fail: ['#ff3030', '#000000'] },
}
