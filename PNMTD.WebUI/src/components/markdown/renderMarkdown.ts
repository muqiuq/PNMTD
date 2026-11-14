import type { Element, ElementContent, Properties, Root, RootContent, Text } from 'hast'
import type { Nodes as MdNodes, Paragraph, Root as MdRoot } from 'mdast'
import bash from 'highlight.js/lib/languages/bash'
import csharp from 'highlight.js/lib/languages/csharp'
import diff from 'highlight.js/lib/languages/diff'
import dockerfile from 'highlight.js/lib/languages/dockerfile'
import ini from 'highlight.js/lib/languages/ini'
import javascript from 'highlight.js/lib/languages/javascript'
import json from 'highlight.js/lib/languages/json'
import nginx from 'highlight.js/lib/languages/nginx'
import powershell from 'highlight.js/lib/languages/powershell'
import python from 'highlight.js/lib/languages/python'
import routeros from 'highlight.js/lib/languages/routeros'
import sql from 'highlight.js/lib/languages/sql'
import xml from 'highlight.js/lib/languages/xml'
import yaml from 'highlight.js/lib/languages/yaml'
import rehypeHighlight from 'rehype-highlight'
import rehypeSanitize, { defaultSchema, type Options as SanitizeSchema } from 'rehype-sanitize'
import remarkGfm from 'remark-gfm'
import remarkParse from 'remark-parse'
import remarkRehype from 'remark-rehype'
import { unified } from 'unified'

/**
 * Markdown to a sanitised HTML tree, for reading (docs/locations-knowledge-plan.md §5.6). The tree is
 * hast; MarkdownView turns it into Vue nodes, so no HTML string is ever put into the page.
 *
 * 1. remark-parse and remark-gfm: CommonMark with tables, task lists, strikethrough, autolinks and
 *    footnotes (§5.1).
 * 2. Raw HTML becomes text before remark-rehype: it is shown as written, never rendered.
 * 3. rehype-sanitize with GitHub's schema, narrowed to what Markdown itself produces: its elements,
 *    their attributes, and http(s), mailto: and tel: links.
 * 4. rehype-highlight for the languages of §5.1, `mermaid` left as plain text.
 * 5. PNMT's steps, on the sanitised tree:
 *    - links: no scheme left → text; external ones open in a new tab; PNMT's pages become router
 *      links; links to /api/v1/files/… that aren't images become attachment chips;
 *    - images: only /api/v1/files/…, lazily, opening the full size; others become a link marked
 *      "external image, not loaded";
 *    - headings get ids with sanitize's prefix (`user-content-`), and #anchors point there;
 *    - code blocks get a copy button, `mermaid` blocks become diagrams (§5.8), tables scroll.
 *
 * The elements of step 5 that are components are `pnmt-*` elements, which only this code creates:
 * `pnmt-code` (properties: source, language), `pnmt-mermaid` (source, index),
 * `pnmt-attachment` (href, fileId, name) and `pnmt-link` (to, the router path).
 */

/** The router's base (vite.config.ts, PNMTD/Hosting/HostingSetup.cs): links under it stay in the app. */
export const appBase = '/pnmt-admin/'

/** A file of PNMT's store (§5.1): /api/v1/files/{id}/{name}. */
export const fileUrl = /^\/api\/v1\/files\/([0-9A-Za-z-]+)\/[^/?#]+$/

/** The prefix of every id from a page, so that a heading can't clobber the app's ids (§5.6). */
export const clobberPrefix = 'user-content-'

/** At most this many diagrams per page draw by themselves; the rest wait for *Show diagram* (§5.8). */
export const maxAutoDiagrams = 50

/** The highlighted languages of §5.1, with the names fences commonly use. */
const languages = {
  bash,
  csharp,
  diff,
  dockerfile,
  ini,
  javascript,
  json,
  nginx,
  powershell,
  python,
  routeros,
  sql,
  xml,
  yaml,
}
const aliases = { bash: ['shell', 'console'], ini: ['toml'], powershell: ['pwsh'], routeros: ['mikrotik'] }

// The elements and attributes mdast-util-to-hast writes for GFM, and nothing else: raw HTML never
// gets here, so GitHub's wider list (div, details, kbd, …) is narrowed to these.
const tagNames = [
  'a',
  'blockquote',
  'br',
  'code',
  'del',
  'em',
  'h1',
  'h2',
  'h3',
  'h4',
  'h5',
  'h6',
  'hr',
  'img',
  'input',
  'li',
  'ol',
  'p',
  'pre',
  'section',
  'strong',
  'sup',
  'table',
  'tbody',
  'td',
  'th',
  'thead',
  'tr',
  'ul',
]
const githubAttributes = defaultSchema.attributes ?? {}
export const sanitizeSchema: SanitizeSchema = {
  ...defaultSchema,
  tagNames,
  attributes: {
    a: githubAttributes.a,
    code: githubAttributes.code,
    h2: githubAttributes.h2,
    img: ['src'],
    input: githubAttributes.input,
    li: githubAttributes.li,
    ol: githubAttributes.ol,
    section: githubAttributes.section,
    ul: githubAttributes.ul,
    '*': ['align', 'alt', 'checked', 'id', 'start', 'title'],
  },
  protocols: {
    href: ['http', 'https', 'mailto', 'tel'],
    src: ['http', 'https'],
  },
  clobberPrefix,
}

/** Raw HTML as text (§5.1): an inline tag stays inline, a block of HTML becomes a paragraph. */
function htmlAsText() {
  return (tree: MdRoot) => {
    const walk = (node: MdNodes) => {
      if (!('children' in node)) {
        return
      }
      const block = node.type === 'root' || node.type === 'blockquote' || node.type === 'listItem'
      node.children = node.children.map((child) => {
        if (child.type !== 'html') {
          walk(child)
          return child
        }
        const text = { type: 'text' as const, value: child.value, position: child.position }
        return block ? ({ type: 'paragraph', children: [text], position: child.position } as Paragraph) : text
      }) as typeof node.children
    }
    walk(tree)
  }
}

const isElement = (node: RootContent | ElementContent | Root): node is Element => node.type === 'element'

function textOf(node: Root | RootContent | ElementContent): string {
  if (node.type === 'text') {
    return node.value
  }
  if ('children' in node) {
    return node.children.map(textOf).join('')
  }
  return ''
}

const text = (value: string): Text => ({ type: 'text', value })

function element(tagName: string, properties: Properties, children: ElementContent[] = []): Element {
  return { type: 'element', tagName, properties, children }
}

/** GitHub's heading slugs: lower case, punctuation dropped, spaces as dashes; repeats get -1, -2, … */
export function slugger() {
  const seen = new Map<string, number>()
  return (value: string) => {
    const base =
      value
        .toLowerCase()
        .trim()
        .replace(/[^\p{L}\p{M}\p{N}\p{Pc}\- ]/gu, '')
        .replace(/ /g, '-') || 'section'
    const count = seen.get(base) ?? 0
    seen.set(base, count + 1)
    return count ? `${base}-${count}` : base
  }
}

/** Another site: http(s), or protocol-relative (//host/…), which sanitize lets through as relative. */
const external = (href: string) => /^(https?:)?\/\//i.test(href)
const externalRel = 'noopener noreferrer nofollow'

/** PNMT's steps (5. above), on the sanitised and highlighted tree. */
function pnmtSteps() {
  return (tree: Root) => {
    const slug = slugger()
    let diagrams = 0

    const rewrite = (node: Element, parents: Element[]): ElementContent[] => {
      const props = node.properties
      switch (node.tagName) {
        case 'a': {
          const href = typeof props.href === 'string' ? props.href : null
          if (href === null) {
            // A scheme sanitize doesn't allow (javascript:, data:, …): the text, not a link.
            return node.children
          }
          if (href.startsWith('#') && !href.startsWith(`#${clobberPrefix}`)) {
            props.href = `#${clobberPrefix}${href.slice(1)}`
          } else if (fileUrl.test(href) && !node.children.some((c) => isElement(c) && c.tagName === 'img')) {
            const [, fileId] = href.match(fileUrl)!
            return [element('pnmt-attachment', { href, fileId, name: textOf(node) })]
          } else if (href === appBase.slice(0, -1) || href.startsWith(appBase)) {
            return [element('pnmt-link', { to: `/${href.slice(appBase.length)}` }, node.children)]
          } else if (external(href)) {
            props.target = '_blank'
            props.rel = externalRel
          }
          return [node]
        }
        case 'img': {
          const src = typeof props.src === 'string' ? props.src : ''
          const alt = typeof props.alt === 'string' ? props.alt : ''
          if (fileUrl.test(src)) {
            props.loading = 'lazy'
            props.decoding = 'async'
            if (parents.some((p) => p.tagName === 'a')) {
              return [node]
            }
            return [element('a', { href: src, target: '_blank', rel: 'noopener', className: ['md-image'] }, [node])]
          }
          const label = alt ? `external image, not loaded: ${alt}` : 'external image, not loaded'
          if (external(src) && !parents.some((p) => p.tagName === 'a')) {
            return [
              element('a', { href: src, target: '_blank', rel: externalRel, className: ['md-external-image'] }, [
                text(label),
              ]),
            ]
          }
          return [element('span', { className: ['md-external-image'] }, [text(src ? label : alt || 'image')])]
        }
        case 'pre': {
          const code = node.children.find((c): c is Element => isElement(c) && c.tagName === 'code')
          const classes = (code?.properties.className as string[] | undefined) ?? []
          const language = classes.find((c) => c.startsWith('language-'))?.slice('language-'.length) ?? ''
          const source = code ? textOf(code) : textOf(node)
          if (language.toLowerCase() === 'mermaid') {
            return [element('pnmt-mermaid', { source, index: diagrams++ })]
          }
          return [element('pnmt-code', { source, language }, [node])]
        }
        case 'table':
          return [element('div', { className: ['md-table'] }, [node])]
        case 'input':
          props.disabled = true
          return [node]
        case 'h1':
        case 'h2':
        case 'h3':
        case 'h4':
        case 'h5':
        case 'h6':
          if (!props.id) {
            props.id = clobberPrefix + slug(textOf(node))
          }
          return [node]
        default:
          return [node]
      }
    }

    const walk = (parent: Root | Element, parents: Element[]) => {
      const next: ElementContent[] = []
      for (const child of parent.children as ElementContent[]) {
        if (!isElement(child)) {
          next.push(child)
          continue
        }
        const inner = [...parents, child]
        walk(child, inner)
        next.push(...rewrite(child, parents))
      }
      parent.children = next as typeof parent.children
    }
    walk(tree, [])
  }
}

const processor = unified()
  .use(remarkParse)
  .use(remarkGfm)
  .use(htmlAsText)
  // Footnote ids without a prefix: sanitize adds it, and the #anchors are pointed there (5. above).
  .use(remarkRehype, { clobberPrefix: '' })
  .use(rehypeSanitize, sanitizeSchema)
  .use(rehypeHighlight, { languages, aliases, plainText: ['mermaid'], detect: false })
  .use(pnmtSteps)
  .freeze()

/** The sanitised tree of a Markdown text (§5.6). */
export function renderMarkdown(markdown: string): Root {
  return processor.runSync(processor.parse(markdown)) as Root
}

/** Whether a Markdown text has a diagram, so a view can say so before Mermaid loads (§5.8). */
export function hasDiagram(markdown: string): boolean {
  return /^ {0,3}(`{3,}|~{3,})\s*mermaid\b/im.test(markdown)
}
