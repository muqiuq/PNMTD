/**
 * Markdown (CommonMark with GFM tables) for downloads of documentation pages, such as the UDP
 * protocol page: from the DOM the page rendered, and helpers for the parts built from data.
 *
 * The converter handles the elements prose is written with: headings, paragraphs, lists, tables,
 * pre-formatted blocks, definition lists, inline code, bold, italics, links, sup and sub. It is not
 * a general HTML converter; the markup it reads can steer it:
 * - `data-md="…"` replaces an element and its content with the given Markdown, even when hidden;
 * - `data-md-skip`, `hidden` and `aria-hidden="true"` elements, buttons and inputs are left out;
 * - a list with `data-md-task` becomes a task list (`- [ ] …`);
 * - a `pre` with `data-md-lang` gets that language on its fence.
 */

const BLOCKS = new Set([
  'ADDRESS',
  'ARTICLE',
  'ASIDE',
  'BLOCKQUOTE',
  'DD',
  'DETAILS',
  'DIV',
  'DL',
  'DT',
  'FIELDSET',
  'FIGURE',
  'FOOTER',
  'FORM',
  'H1',
  'H2',
  'H3',
  'H4',
  'H5',
  'H6',
  'HEADER',
  'HR',
  'LI',
  'MAIN',
  'NAV',
  'OL',
  'P',
  'PRE',
  'SECTION',
  'SUMMARY',
  'TABLE',
  'UL',
])
const LEFT_OUT = new Set(['BUTTON', 'INPUT', 'NOSCRIPT', 'SCRIPT', 'SELECT', 'STYLE', 'SVG', 'TEMPLATE', 'TEXTAREA'])

const tag = (el: Element) => el.tagName.toUpperCase()

function leftOut(el: Element): boolean {
  return (
    LEFT_OUT.has(tag(el)) ||
    el.hasAttribute('data-md-skip') ||
    el.hasAttribute('hidden') ||
    el.getAttribute('aria-hidden') === 'true'
  )
}

/** Text as Markdown: the characters that would start markup, escaped. Underscores inside words stay. */
export function mdText(text: string): string {
  return text
    .replace(/[\\`*[\]<]/g, '\\$&')
    .replace(/_/g, (_, i: number, s: string) => (/\w/.test(s[i - 1] ?? '') && /\w/.test(s[i + 1] ?? '') ? '_' : '\\_'))
}

/** Inline code, with a longer delimiter when the text has backticks. */
export function mdCode(text: string): string {
  const content = text.replace(/\s+/g, ' ')
  if (!content.trim()) return ''
  if (!content.includes('`')) return `\`${content}\``
  const longest = Math.max(...Array.from(content.matchAll(/`+/g), (m) => m[0].length))
  const fence = '`'.repeat(longest + 1)
  return `${fence} ${content} ${fence}`
}

/** A fenced code block; the fence outgrows any run of backticks in the code. */
export function mdFence(code: string, lang = ''): string {
  const longest = Math.max(2, ...Array.from(code.matchAll(/`+/g), (m) => m[0].length))
  const fence = '`'.repeat(longest + 1)
  return `${fence}${lang}\n${code.replace(/\n+$/, '')}\n${fence}`
}

/** A table cell: one line, its pipes escaped (inside code too, as GFM wants). */
function cellText(markdown: string): string {
  return markdown
    .trim()
    .replace(/\s*\n\s*/g, '<br>')
    .replace(/\|/g, '\\|')
}

/** A GFM table; cells are Markdown already (use mdText or mdCode for raw values). */
export function mdTable(header: string[], rows: string[][]): string {
  const width = Math.max(header.length, ...rows.map((r) => r.length))
  const line = (cells: string[]) =>
    `| ${Array.from({ length: width }, (_, i) => cellText(cells[i] ?? '') || ' ').join(' | ')} |`
  return [line(header), `|${' --- |'.repeat(width)}`, ...rows.map(line)].join('\n')
}

/** Wraps the trimmed text in a marker, the surrounding spaces outside it: ` **a** `, not `** a **`. */
function emphasis(marker: string, text: string): string {
  const m = /^(\s*)([\s\S]*?)(\s*)$/.exec(text)!
  return m[2] ? `${m[1]}${marker}${m[2]}${marker}${m[3]}` : text
}

function inline(node: Node): string {
  if (node.nodeType === Node.TEXT_NODE) {
    return mdText((node.textContent ?? '').replace(/\s+/g, ' '))
  }
  if (node.nodeType !== Node.ELEMENT_NODE) return ''
  const el = node as Element
  const override = el.getAttribute('data-md')
  if (override !== null) return override
  if (leftOut(el)) return ''
  const inner = () => Array.from(el.childNodes, inline).join('')
  switch (tag(el)) {
    case 'CODE':
    case 'KBD':
    case 'SAMP':
      return mdCode(el.textContent ?? '')
    case 'STRONG':
    case 'B':
      return emphasis('**', inner())
    case 'EM':
    case 'I':
      return emphasis('*', inner())
    case 'A': {
      const href = el.getAttribute('href')
      const text = inner().trim()
      return href && text ? `[${text}](${href})` : text
    }
    case 'BR':
      return '\n'
    case 'SUP':
      return `<sup>${inner()}</sup>`
    case 'SUB':
      return `<sub>${inner()}</sub>`
    default:
      // A block inside an inline element: its blocks, on lines of their own.
      return BLOCKS.has(tag(el)) ? `\n${blocks(el).join('\n')}\n` : inner()
  }
}

/** A paragraph from inline Markdown: single spaces, no blank edges, line breaks kept as hard breaks. */
function paragraph(markdown: string): string {
  return markdown
    .split('\n')
    .map((l) => l.replace(/[ \t]+/g, ' ').trim())
    .filter(Boolean)
    .join('  \n')
}

const LIST_ITEM = /^([-*+]|\d+\.) /

function list(el: Element): string {
  const ordered = tag(el) === 'OL'
  let n = Number(el.getAttribute('start') ?? 1)
  const items: string[] = []
  for (const li of Array.from(el.children)) {
    if (tag(li) !== 'LI' || leftOut(li)) continue
    const marker = ordered ? `${n++}.` : el.hasAttribute('data-md-task') ? '- [ ]' : '-'
    const indent = ' '.repeat(ordered ? marker.length + 1 : 2)
    // Tight: a nested list follows its line directly; other blocks get a blank line.
    const body = blocks(li)
      .map((part, i) => (i === 0 ? part : LIST_ITEM.test(part) ? `\n${part}` : `\n\n${part}`))
      .join('')
    const lines = body.split('\n').map((l, i) => (i === 0 || !l ? l : indent + l))
    items.push(`${marker} ${lines.join('\n')}`)
  }
  return items.join('\n')
}

function table(el: HTMLTableElement): string {
  const rows = Array.from(el.rows).filter((r) => !leftOut(r))
  if (!rows.length) return ''
  const headRow = el.tHead?.rows[0]
  const head = headRow && !leftOut(headRow) ? headRow : rows[0]!
  const cells = (r: HTMLTableRowElement) =>
    Array.from(r.cells)
      .filter((c) => !leftOut(c))
      .map((c) => blocks(c).join('\n'))
  return mdTable(cells(head), rows.filter((r) => r !== head).map(cells))
}

function definitions(el: Element): string {
  const lines: string[] = []
  for (const child of Array.from(el.children)) {
    if (leftOut(child)) continue
    const text = blocks(child).join(' ')
    if (tag(child) === 'DT') lines.push(`- **${text}**`)
    else if (tag(child) === 'DD' && lines.length) lines[lines.length - 1] += `: ${text}`
  }
  return lines.join('\n')
}

/** The Markdown blocks of an element's content. */
function blocks(container: Element): string[] {
  const out: string[] = []
  let pending = ''
  const flush = () => {
    const text = paragraph(pending)
    if (text) out.push(text)
    pending = ''
  }
  for (const node of Array.from(container.childNodes)) {
    const el = node.nodeType === Node.ELEMENT_NODE ? (node as Element) : null
    if (!el || !BLOCKS.has(tag(el)) || el.hasAttribute('data-md') || leftOut(el)) {
      pending += inline(node)
      continue
    }
    flush()
    out.push(...blockOf(el).filter(Boolean))
  }
  flush()
  return out
}

function blockOf(el: Element): string[] {
  const name = tag(el)
  if (/^H[1-6]$/.test(name)) {
    const text = paragraph(blocks(el).join(' ')).replace(/ {2}\n/g, ' ')
    return [text ? `${'#'.repeat(Number(name[1]))} ${text}` : '']
  }
  switch (name) {
    case 'P':
      return [paragraph(Array.from(el.childNodes, inline).join(''))]
    case 'PRE':
      return [mdFence(el.textContent ?? '', el.getAttribute('data-md-lang') ?? '')]
    case 'UL':
    case 'OL':
      return [list(el)]
    case 'TABLE':
      return [table(el as HTMLTableElement)]
    case 'DL':
      return [definitions(el)]
    case 'HR':
      return ['---']
    case 'BLOCKQUOTE':
      return [
        blocks(el)
          .join('\n\n')
          .split('\n')
          .map((l) => (l ? `> ${l}` : '>'))
          .join('\n'),
      ]
    default:
      return blocks(el)
  }
}

/** The Markdown of an element's content, blocks separated by blank lines. */
export function htmlToMarkdown(root: Element): string {
  return blocks(root).join('\n\n')
}

/** Saves text as a file through the browser's download. */
export function downloadText(fileName: string, text: string, type = 'text/markdown;charset=utf-8'): void {
  const url = URL.createObjectURL(new Blob([text], { type }))
  const link = document.createElement('a')
  link.href = url
  link.download = fileName
  link.style.display = 'none'
  document.body.append(link)
  link.click()
  link.remove()
  // Once the click has started the download.
  setTimeout(() => URL.revokeObjectURL(url), 1000)
}

/** A file name part from a title: lowercase letters, digits and dashes. */
export function fileSlug(title: string): string {
  return title
    .normalize('NFKD')
    .replace(/[\u0300-\u036f]/g, '')
    .toLowerCase()
    .replace(/[^a-z0-9]+/g, '-')
    .replace(/^-+|-+$/g, '')
}
