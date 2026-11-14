import type { Element, ElementContent, Root } from 'hast'
import { createPinia, setActivePinia } from 'pinia'
import { beforeEach, describe, expect, it } from 'vitest'
import { mountWithUi } from '@/test/mount'
import MarkdownView from './MarkdownView.vue'
import { renderMarkdown } from './renderMarkdown'

// docs/locations-knowledge-plan.md §12: nothing executable comes out, external images become links,
// PNMT's images stay, file links become chips.

function elements(node: Root | Element, out: Element[] = []): Element[] {
  for (const child of node.children as ElementContent[]) {
    if (child.type === 'element') {
      out.push(child)
      elements(child, out)
    }
  }
  return out
}

const dangerousUrl = /^\s*(javascript|vbscript|data|file):/i

/** Everything an attack could leave behind, in the tree and in the DOM. */
function assertInert(markdown: string) {
  const tree = renderMarkdown(markdown)
  for (const el of elements(tree)) {
    expect([
      'script',
      'style',
      'iframe',
      'object',
      'embed',
      'svg',
      'math',
      'form',
      'base',
      'meta',
      'link',
    ]).not.toContain(el.tagName)
    for (const [key, value] of Object.entries(el.properties)) {
      expect(key.toLowerCase().startsWith('on'), `${el.tagName}.${key}`).toBe(false)
      if (['href', 'src', 'action', 'formAction', 'xlinkHref'].includes(key)) {
        expect(String(value)).not.toMatch(dangerousUrl)
      }
    }
  }

  const view = mountWithUi(MarkdownView, { props: { source: markdown } })
  const root = view.element as HTMLElement
  expect(root.querySelectorAll('script, style, iframe, object, embed, svg, math, form, base, meta, link')).toHaveLength(
    0,
  )
  for (const el of Array.from(root.querySelectorAll('*'))) {
    for (const attr of Array.from(el.attributes)) {
      expect(attr.name.startsWith('on'), `${el.tagName} ${attr.name}`).toBe(false)
      if (['href', 'src', 'action', 'formaction', 'xlink:href'].includes(attr.name)) {
        expect(attr.value).not.toMatch(dangerousUrl)
      }
    }
  }
  view.unmount()
  return root
}

const corpus = [
  '[click](javascript:alert(1))',
  '[click](JaVaScRiPt:alert(1))',
  '[click](  javascript:alert(1))',
  '[click](java&#09;script:alert(1))',
  '[click](&#106;avascript:alert(1))',
  '[click](javascript&colon;alert(1))',
  '[click](vbscript:msgbox(1))',
  '[click](data:text/html;base64,PHNjcmlwdD5hbGVydCgxKTwvc2NyaXB0Pg==)',
  '[click](file:///etc/passwd)',
  '<javascript:alert(1)>',
  '[ref]: javascript:alert(1)\n\n[click][ref]',
  '![x](javascript:alert(1))',
  '![x](data:image/svg+xml;base64,PHN2ZyBvbmxvYWQ9YWxlcnQoMSk+)',
  '<script>alert(1)</script>',
  '<img src=x onerror=alert(1)>',
  'Inline <img src=x onerror=alert(1)> image',
  '<svg onload=alert(1)><circle r=5 /></svg>',
  '<a href="javascript:alert(1)">x</a>',
  '<iframe src="https://evil.example"></iframe>',
  '<style>body { display: none }</style>',
  '<form action="https://evil.example"><input name=x></form>',
  '<details open ontoggle=alert(1)>x</details>',
  '<math><mi xlink:href="javascript:alert(1)">x</mi></math>',
  '&lt;script&gt;alert(1)&lt;/script&gt;',
  '<!-- <script>alert(1)</script> -->',
  '[x](https://example.com "t\\" onmouseover=\\"alert(1)")',
  '```html\n<script>alert(1)</script>\n```',
  '| a |\n|---|\n| <img src=x onerror=alert(1)> |',
  '- [ ] <b onclick=alert(1)>task</b>',
  '[^1]\n\n[^1]: <script>alert(1)</script>',
]

describe('renderMarkdown', () => {
  beforeEach(() => setActivePinia(createPinia()))

  it.each(corpus)('leaves nothing executable: %s', (markdown) => {
    assertInert(markdown)
  })

  it('shows raw HTML as text', () => {
    const root = assertInert('<script>alert(1)</script>\n\nand <b>bold</b>')
    expect(root.textContent).toContain('<script>alert(1)</script>')
    expect(root.textContent).toContain('<b>bold</b>')
    expect(root.querySelector('b')).toBeNull()
  })

  it('renders the visual editor’s empty lines (a bare <br />) as line breaks, not text', () => {
    // What Crepe saves for "This is nice", two empty lines, then an image.
    const root = assertInert('This is nice\n\n<br />\n\n<BR>\n\nline one<br/>line two')
    expect(root.textContent).not.toContain('<br')
    expect(root.textContent).not.toContain('<BR')
    const paragraphs = Array.from(root.querySelectorAll('p'))
    expect(paragraphs.map((p) => p.innerHTML.replace(/\n/g, ''))).toEqual([
      'This is nice',
      '<br>',
      '<br>',
      'line one<br>line two',
    ])
  })

  it('keeps a <br> with anything more as text', () => {
    const root = assertInert('<br onclick="alert(1)">\n\na <br class="x"> b')
    expect(root.querySelectorAll('br')).toHaveLength(0)
    expect(root.textContent).toContain('<br onclick="alert(1)">')
    expect(root.textContent).toContain('<br class="x">')
  })

  it('breaks the line at a bare <br> next to other HTML, which stays text', () => {
    const root = assertInert('<br><script>alert(1)</script>')
    expect(root.querySelectorAll('br')).toHaveLength(1)
    expect(root.textContent).toContain('<script>alert(1)</script>')
  })

  it('makes a link with a forbidden scheme its text', () => {
    const root = assertInert('[click me](javascript:alert(1))')
    expect(root.querySelector('a')).toBeNull()
    expect(root.textContent).toBe('click me')
  })

  it('keeps http(s), mailto: and tel: links; external ones open in a new tab', () => {
    const view = mountWithUi(MarkdownView, {
      props: { source: '[web](https://example.com) [mail](mailto:a@example.com) [phone](tel:+41441234567)' },
    })
    const [web, mail, phone] = view.findAll('a')
    expect(web!.attributes('href')).toBe('https://example.com')
    expect(web!.attributes('target')).toBe('_blank')
    expect(web!.attributes('rel')).toBe('noopener noreferrer nofollow')
    expect(mail!.attributes('href')).toBe('mailto:a@example.com')
    expect(phone!.attributes('href')).toBe('tel:+41441234567')
    view.unmount()
  })

  it('treats a protocol-relative link as external', () => {
    const view = mountWithUi(MarkdownView, { props: { source: '[x](//evil.example/path)' } })
    expect(view.find('a').attributes('target')).toBe('_blank')
    view.unmount()
  })

  it("shows PNMT's images, lazily, and other images as links that aren't loaded", () => {
    const view = mountWithUi(MarkdownView, {
      props: {
        source: '![Cage B](/api/v1/files/0193f3c2-aa/cage-b.png)\n\n![logo](https://tracker.example/pixel.png)',
      },
    })
    const images = view.findAll('img')
    expect(images).toHaveLength(1)
    expect(images[0]!.attributes('src')).toBe('/api/v1/files/0193f3c2-aa/cage-b.png')
    expect(images[0]!.attributes('loading')).toBe('lazy')
    const external = view.find('a.md-external-image')
    expect(external.attributes('href')).toBe('https://tracker.example/pixel.png')
    expect(external.text()).toBe('external image, not loaded: logo')
    view.unmount()
  })

  it('makes links to files that are not images attachment chips, with their size when known', () => {
    const files = [{ id: '0194ab', name: 'ups-manual.pdf', size: 2_516_582, contentType: 'application/pdf' }]
    const view = mountWithUi(MarkdownView, {
      props: {
        source: '[UPS manual.pdf](/api/v1/files/0194ab/ups-manual.pdf) and [old](/api/v1/files/0195cd/old.zip)',
        files,
      },
    })
    const chips = view.findAll('.attachment-chip')
    expect(chips).toHaveLength(2)
    expect(chips[0]!.attributes('href')).toBe('/api/v1/files/0194ab/ups-manual.pdf')
    expect(chips[0]!.attributes('download')).toBeDefined()
    expect(chips[0]!.text()).toContain('UPS manual.pdf')
    expect(chips[0]!.text()).toContain('2.4 MB')
    // Not among the page's files: removed.
    expect(chips[1]!.text()).toContain('File removed')
    view.unmount()
  })

  it("routes links to PNMT's pages through the router", () => {
    const tree = renderMarkdown('[KB-12](/pnmt-admin/kb/12)')
    const link = elements(tree).find((e) => e.tagName === 'pnmt-link')
    expect(link?.properties.to).toBe('/kb/12')
  })

  it("prefixes heading ids and #anchors, so a page can't clobber the app's ids", () => {
    const view = mountWithUi(MarkdownView, {
      props: { source: '# app\n\n## Drivers & queues\n\n## app\n\n[up](#app)' },
    })
    const ids = view.findAll('h1, h2').map((h) => h.attributes('id'))
    expect(ids).toEqual(['user-content-app', 'user-content-drivers--queues', 'user-content-app-1'])
    expect(view.find('a').attributes('href')).toBe('#user-content-app')
    view.unmount()
  })

  it('prefixes footnote ids and their links alike', () => {
    const view = mountWithUi(MarkdownView, { props: { source: 'Text[^n].\n\n[^n]: The note.' } })
    const ref = view.find('a[data-footnote-ref]')
    const target = ref.attributes('href')!.slice(1)
    expect(target.startsWith('user-content-')).toBe(true)
    expect(view.find(`[id="${target}"]`).exists()).toBe(true)
    view.unmount()
  })

  it('shows task boxes disabled', () => {
    const view = mountWithUi(MarkdownView, { props: { source: '- [x] done\n- [ ] open' } })
    const boxes = view.findAll('input[type="checkbox"]')
    expect(boxes).toHaveLength(2)
    for (const box of boxes) {
      expect((box.element as HTMLInputElement).disabled).toBe(true)
    }
    expect((boxes[0]!.element as HTMLInputElement).checked).toBe(true)
    view.unmount()
  })

  it('highlights code, with a copy button, and wraps tables to scroll', () => {
    const view = mountWithUi(MarkdownView, {
      props: { source: '```bash\necho "hi"\n```\n\n| a | b |\n|---|---|\n| 1 | 2 |' },
    })
    expect(view.find('.md-code button[aria-label="Copy the bash code"]').exists()).toBe(true)
    expect(view.find('pre code.language-bash .hljs-built_in, pre code.language-bash .hljs-string').exists()).toBe(true)
    expect(view.find('.md-table > table').exists()).toBe(true)
    view.unmount()
  })

  it('leaves mermaid blocks for the diagram component, with their source', () => {
    const tree = renderMarkdown('```mermaid\nflowchart LR\n  a --> b\n```')
    const diagram = elements(tree).find((e) => e.tagName === 'pnmt-mermaid')
    expect(diagram?.properties.source).toBe('flowchart LR\n  a --> b\n')
    expect(elements(tree).some((e) => e.tagName === 'pre')).toBe(false)
  })

  it('numbers diagrams, so that only the first 50 draw by themselves', () => {
    const tree = renderMarkdown(
      Array.from({ length: 52 }, (_, i) => `\`\`\`mermaid\npie\n"x": ${i}\n\`\`\``).join('\n\n'),
    )
    const indexes = elements(tree)
      .filter((e) => e.tagName === 'pnmt-mermaid')
      .map((e) => e.properties.index)
    expect(indexes).toHaveLength(52)
    expect(indexes[51]).toBe(51)
  })
})
