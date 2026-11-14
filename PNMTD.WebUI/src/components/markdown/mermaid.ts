import DOMPurify from 'dompurify'
import type { Mermaid, MermaidConfig } from 'mermaid'

/**
 * Mermaid for ` ```mermaid ` blocks (docs/locations-knowledge-plan.md §5.8), loaded only when a page
 * has a diagram: MermaidDiagram imports this module lazily, and this module imports Mermaid lazily,
 * so each diagram type's code loads on first use.
 *
 * Safety, in order:
 * 1. securityLevel 'strict': text encoded, click actions and links off;
 * 2. htmlLabels false: labels are SVG text, not HTML inside the SVG;
 * 3. Mermaid's `secure` list extended with htmlLabels, themeCSS and dompurifyConfig: a page's
 *    %%{init}%% directives and front matter can change the theme and layout, never those nor the
 *    security level (Mermaid removes secure keys at every depth, so flowchart.htmlLabels too);
 * 4. the SVG sanitised again with DOMPurify's SVG profile: no scripts, no event attributes, no
 *    foreignObject, no references outside the SVG (href only to #ids, no url() but url(#…), no
 *    @import);
 * 5. nothing fetched: no icon packs are registered, no web fonts named;
 * 6. maxTextSize 50,000 and maxEdges 500; above them the source is shown, *Too large to draw*.
 *
 * Mermaid draws in the page's thread with global settings, so drawings run one after the other.
 * Results are kept per source and theme.
 */

export const maxTextSize = 50_000
export const maxEdges = 500

/** Mermaid's default secure keys, and the three of §5.8. */
export const secureKeys = [
  'secure',
  'securityLevel',
  'startOnLoad',
  'maxTextSize',
  'suppressErrorRendering',
  'maxEdges',
  'htmlLabels',
  'themeCSS',
  'dompurifyConfig',
]

/** The colours and font of a diagram, from PNMT's tokens (readTheme). */
export interface DiagramTheme {
  dark: boolean
  font: string
  panel: string
  background: string
  text: string
  muted: string
  border: string
  primary: string
  primarySoft: string
}

export const lightTheme: DiagramTheme = {
  dark: false,
  font: "'Inter Variable', system-ui, sans-serif",
  panel: '#ffffff',
  background: '#f1f5f9',
  text: '#0f172a',
  muted: '#334155',
  border: '#cbd5e1',
  primary: '#3b59a5',
  primarySoft: '#dfe6f5',
}

export const darkTheme: DiagramTheme = {
  dark: true,
  font: "'Inter Variable', system-ui, sans-serif",
  panel: '#18181b',
  background: '#09090b',
  text: '#ffffff',
  muted: '#a1a1aa',
  border: '#3f3f46',
  primary: '#8ea3d6',
  primarySoft: '#22325c',
}

/** The settings of §5.8 for one theme. Mermaid's `base` theme takes its variables from the tokens. */
export function mermaidConfig(theme: DiagramTheme): MermaidConfig {
  return {
    startOnLoad: false,
    securityLevel: 'strict',
    htmlLabels: false,
    // Deprecated for the root-level setting, but some diagram types still read their own.
    flowchart: { htmlLabels: false },
    // These two place their texts in foreignObject by default, which the sanitiser removes (K0).
    journey: { textPlacement: 'tspan' },
    timeline: { textPlacement: 'tspan' },
    secure: secureKeys,
    maxTextSize,
    maxEdges,
    suppressErrorRendering: true,
    deterministicIds: false,
    theme: 'base',
    fontFamily: theme.font,
    themeVariables: {
      darkMode: theme.dark,
      fontFamily: theme.font,
      fontSize: '14px',
      background: theme.panel,
      primaryColor: theme.primarySoft,
      primaryTextColor: theme.text,
      primaryBorderColor: theme.primary,
      secondaryColor: theme.background,
      tertiaryColor: theme.panel,
      lineColor: theme.muted,
      textColor: theme.text,
      mainBkg: theme.primarySoft,
      nodeBorder: theme.primary,
      clusterBkg: theme.background,
      clusterBorder: theme.border,
      titleColor: theme.text,
      edgeLabelBackground: theme.panel,
      // Bar and line charts: the primary colour, then amber and green. Mermaid's own palette tints primaryColor, which is
      // the soft one here: bars and lines nearly vanished on the panel (K7).
      xyChart: { plotColorPalette: `${theme.primary}, ${theme.dark ? '#f0b35a, #7cc4a4' : '#d97706, #15803d'}` },
    },
  }
}

// --- the SVG, sanitised again -------------------------------------------------------------------

const localUrl = /url\(\s*(['"]?)\s*#/i

/** CSS from a diagram, without what reaches outside the SVG: url() other than url(#…), @import. */
export function cleanCss(css: string): string {
  return css
    .replace(/@import[^;]*;?/gi, '')
    .replace(/url\(\s*(['"]?)([^)]*)\1\s*\)/gi, (match) => (localUrl.test(match) ? match : 'none'))
    .replace(/expression\s*\(/gi, '(')
}

let purifier: ReturnType<typeof DOMPurify> | null = null

function svgPurifier() {
  if (purifier) {
    return purifier
  }
  const p = DOMPurify(window)
  p.addHook('uponSanitizeAttribute', (_node, data) => {
    const name = data.attrName.toLowerCase()
    if ((name === 'href' || name === 'xlink:href') && !data.attrValue.trim().startsWith('#')) {
      data.keepAttr = false
    } else if (name === 'style') {
      data.attrValue = cleanCss(data.attrValue)
    }
  })
  p.addHook('uponSanitizeElement', (node, data) => {
    if (data.tagName === 'style' && node.textContent) {
      node.textContent = cleanCss(node.textContent)
    }
  })
  purifier = p
  return p
}

/** A diagram's SVG through DOMPurify's SVG profile (§5.8, 4.). */
export function sanitizeSvg(svg: string): string {
  return svgPurifier().sanitize(svg, {
    USE_PROFILES: { svg: true, svgFilters: true },
    // Sequence and C4 diagrams draw their symbols with <use xlink:href="#…">; the hook keeps it to #ids.
    ADD_TAGS: ['use'],
    FORBID_TAGS: ['foreignObject', 'script', 'iframe', 'object', 'embed'],
    // Event attributes are never on the SVG profile's list; these are listed so a change of DOMPurify can't add them.
    FORBID_ATTR: ['onload', 'onerror', 'onclick', 'onmouseover', 'onfocus', 'onbegin', 'onend'],
    ALLOW_DATA_ATTR: false,
  }) as string
}

// --- drawing ----------------------------------------------------------------------------------

export type DiagramResult =
  | { ok: true; svg: string; title: string | null; description: string | null }
  | { ok: false; message: string; tooLarge: boolean }

const cache = new Map<string, DiagramResult>()
const cacheSize = 200
let mermaid: Promise<Mermaid> | null = null
let queue: Promise<unknown> = Promise.resolve()
let counter = 0

function loadMermaid(): Promise<Mermaid> {
  mermaid ??= import('mermaid').then((m) => m.default)
  return mermaid
}

function remember(key: string, result: DiagramResult): DiagramResult {
  if (cache.size >= cacheSize) {
    cache.delete(cache.keys().next().value!)
  }
  cache.set(key, result)
  return result
}

/** A cached result, if the diagram was drawn before in this theme. */
export function cachedDiagram(source: string, theme: DiagramTheme): DiagramResult | undefined {
  return cache.get(`${JSON.stringify(theme)}\n${source}`)
}

function firstText(svg: string, tag: 'title' | 'desc'): string | null {
  const match = svg.match(new RegExp(`<${tag}[^>]*>([\\s\\S]*?)</${tag}>`, 'i'))
  if (!match) {
    return null
  }
  const box = document.createElement('textarea')
  box.innerHTML = match[1]!
  return box.value.trim() || null
}

/**
 * Draws a diagram, or says why not: Mermaid's message (with its line) for a syntax error, *too large*
 * above the limits. Never throws.
 */
export function drawDiagram(source: string, theme: DiagramTheme): Promise<DiagramResult> {
  const key = `${JSON.stringify(theme)}\n${source}`
  const known = cache.get(key)
  if (known) {
    return Promise.resolve(known)
  }
  if (source.length > maxTextSize) {
    return Promise.resolve({
      ok: false,
      tooLarge: true,
      message: `${source.length.toLocaleString('en')} characters; at most ${maxTextSize.toLocaleString('en')} are drawn.`,
    })
  }
  const run = queue.then(async (): Promise<DiagramResult> => {
    const again = cache.get(key)
    if (again) {
      return again
    }
    const id = `pnmt-diagram-${++counter}`
    try {
      const m = await loadMermaid()
      m.initialize(mermaidConfig(theme))
      const { svg } = await m.render(id, source)
      const clean = sanitizeSvg(svg)
      return remember(key, {
        ok: true,
        svg: clean,
        title: firstText(clean, 'title'),
        description: firstText(clean, 'desc'),
      })
    } catch (e) {
      const message = e instanceof Error ? e.message : String(e)
      return remember(key, {
        ok: false,
        message,
        tooLarge: /edge limit|maximum text size/i.test(message),
      })
    } finally {
      // Mermaid's scratch element for measuring stays behind after an error.
      document.getElementById(`d${id}`)?.remove()
      document.getElementById(id)?.remove()
    }
  })
  queue = run.catch(() => undefined)
  return run
}

// --- the theme from PNMT's tokens ---------------------------------------------------------------

/**
 * A token's colour as rgb(): resolved by the browser on a probe element, then normalised by a
 * canvas, so that what color-mix() or a palette variable gives becomes something Mermaid's colour
 * library reads. The fallback when the browser can't tell.
 */
function tokenColor(probe: HTMLElement, token: string, fallback: string): string {
  probe.style.color = ''
  probe.style.color = `var(${token})`
  const computed = getComputedStyle(probe).color
  const canvas = document.createElement('canvas').getContext('2d')
  if (canvas) {
    canvas.fillStyle = '#000001'
    canvas.fillStyle = computed
    const normal = String(canvas.fillStyle)
    if (/^#[0-9a-f]{6}$/i.test(normal) || /^rgba?\(/i.test(normal)) {
      return normal === '#000001' ? fallback : normal
    }
  }
  return /^(#|rgba?\()/i.test(computed) ? computed : fallback
}

/** The diagram theme of what the page shows now (the dark class on <html>, base.css's tokens). */
export function readTheme(dark: boolean): DiagramTheme {
  const fallback = dark ? darkTheme : lightTheme
  if (typeof document === 'undefined' || !document.body) {
    return fallback
  }
  const probe = document.createElement('span')
  probe.style.display = 'none'
  document.body.appendChild(probe)
  try {
    const font = getComputedStyle(document.documentElement).getPropertyValue('--pnmt-font').trim()
    return {
      dark,
      font: font || fallback.font,
      panel: tokenColor(probe, '--pnmt-panel', fallback.panel),
      background: tokenColor(probe, '--pnmt-bg', fallback.background),
      text: tokenColor(probe, '--pnmt-text', fallback.text),
      muted: tokenColor(probe, '--pnmt-muted', fallback.muted),
      border: tokenColor(probe, '--pnmt-border', fallback.border),
      primary: tokenColor(probe, dark ? '--p-primary-300' : '--p-primary-500', fallback.primary),
      primarySoft: tokenColor(probe, dark ? '--p-primary-900' : '--p-primary-100', fallback.primarySoft),
    }
  } finally {
    probe.remove()
  }
}
