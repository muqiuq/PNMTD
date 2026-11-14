import mermaid from 'mermaid'
import { beforeAll, describe, expect, it } from 'vitest'
import {
  cleanCss,
  drawDiagram,
  lightTheme,
  maxEdges,
  maxTextSize,
  mermaidConfig,
  sanitizeSvg,
  secureKeys,
} from './mermaid'

// docs/locations-knowledge-plan.md §5.8 and §12: XSS through Mermaid. click with javascript:, HTML
// labels, %%{init}%% setting securityLevel loose, htmlLabels or themeCSS, and foreignObject: none
// takes effect, and the SVG has no script or event attribute.

// jsdom lays nothing out: SVG measurements as a browser without fonts would give them.
beforeAll(() => {
  const proto = window.SVGElement.prototype as unknown as Record<string, unknown>
  proto.getBBox ??= function (this: SVGElement) {
    const text = this.textContent ?? ''
    return { x: 0, y: 0, width: 8 * text.length + 1, height: 16 }
  }
  proto.getComputedTextLength ??= function (this: SVGElement) {
    return 8 * (this.textContent ?? '').length
  }
  proto.getTotalLength ??= () => 100
  proto.getPointAtLength ??= () => ({ x: 0, y: 0 })
})

function assertClean(svg: string) {
  const box = document.createElement('div')
  box.innerHTML = svg
  expect(box.querySelectorAll('script, foreignObject, foreignobject, iframe, object, embed, img, div')).toHaveLength(0)
  for (const el of Array.from(box.querySelectorAll('*'))) {
    for (const attr of Array.from(el.attributes)) {
      expect(attr.name.toLowerCase().startsWith('on'), `${el.tagName} ${attr.name}`).toBe(false)
      if (/href$/i.test(attr.name)) {
        expect(attr.value.trim().startsWith('#'), `${el.tagName} ${attr.name}=${attr.value}`).toBe(true)
      }
    }
  }
  expect(svg).not.toMatch(/javascript:/i)
}

describe('the settings', () => {
  it('are strict, without HTML labels, with the extended secure list and the limits', () => {
    const config = mermaidConfig(lightTheme)
    expect(config.securityLevel).toBe('strict')
    expect(config.htmlLabels).toBe(false)
    expect(config.flowchart?.htmlLabels).toBe(false)
    expect(config.secure).toEqual(
      expect.arrayContaining(['securityLevel', 'htmlLabels', 'themeCSS', 'dompurifyConfig']),
    )
    expect(secureKeys).toEqual(expect.arrayContaining(['secure', 'maxTextSize', 'maxEdges']))
    expect(config.maxTextSize).toBe(50_000)
    expect(maxTextSize).toBe(50_000)
    expect(config.maxEdges).toBe(500)
    expect(maxEdges).toBe(500)
    expect(config.theme).toBe('base')
    expect(config.startOnLoad).toBe(false)
  })
})

describe('directives', () => {
  const loosen = [
    '%%{init: {"securityLevel": "loose"}}%%',
    "%%{init: {'securityLevel': 'loose', 'htmlLabels': true}}%%",
    '%%{init: {"htmlLabels": true, "flowchart": {"htmlLabels": true}}}%%',
    '%%{init: {"themeCSS": ".node rect { fill: url(https://evil.example/x) }"}}%%',
    '%%{init: {"dompurifyConfig": {"ADD_TAGS": ["script"]}}}%%',
    '%%{init: {"secure": []}}%%',
    '---\nconfig:\n  securityLevel: loose\n  htmlLabels: true\n  themeCSS: "* { background: red }"\n---',
  ]

  it.each(loosen)("can't loosen the settings: %s", async (directive) => {
    mermaid.initialize(mermaidConfig(lightTheme))
    await mermaid.parse(`${directive}\nflowchart LR\n  a --> b`)
    const config = mermaid.mermaidAPI.getConfig()
    expect(config.securityLevel).toBe('strict')
    expect(config.htmlLabels).toBe(false)
    expect(config.flowchart?.htmlLabels).toBe(false)
    expect(config.themeCSS).toBeUndefined()
    expect(config.dompurifyConfig).toBeUndefined()
    expect(config.secure).toEqual(secureKeys)
  })

  it('may still change the theme and the layout', async () => {
    mermaid.initialize(mermaidConfig(lightTheme))
    await mermaid.parse('%%{init: {"theme": "forest", "flowchart": {"curve": "step"}}}%%\nflowchart LR\n  a --> b')
    const config = mermaid.mermaidAPI.getConfig()
    expect(config.theme).toBe('forest')
    expect(config.flowchart?.curve).toBe('step')
  })
})

describe('the SVG, sanitised again', () => {
  const attacks = [
    '<svg xmlns="http://www.w3.org/2000/svg"><script>alert(1)</script><g/></svg>',
    '<svg xmlns="http://www.w3.org/2000/svg" onload="alert(1)"><rect onclick="alert(1)" onmouseover="alert(1)"/></svg>',
    '<svg xmlns="http://www.w3.org/2000/svg"><foreignObject><div onclick="alert(1)">x</div><img src=x onerror=alert(1)></foreignObject></svg>',
    '<svg xmlns="http://www.w3.org/2000/svg" xmlns:xlink="http://www.w3.org/1999/xlink"><a xlink:href="javascript:alert(1)"><text>x</text></a><a href="javascript:alert(1)"><text>y</text></a></svg>',
    '<svg xmlns="http://www.w3.org/2000/svg"><image href="https://evil.example/pixel.png"/><use href="https://evil.example/s.svg#x"/></svg>',
    '<svg xmlns="http://www.w3.org/2000/svg"><animate attributeName="href" to="javascript:alert(1)"/><set attributeName="onmouseover" to="alert(1)"/></svg>',
    '<svg xmlns="http://www.w3.org/2000/svg"><iframe src="javascript:alert(1)"></iframe><embed src="x"/></svg>',
  ]

  it.each(attacks)('removes what could run or reach out: %s', (svg) => {
    assertClean(sanitizeSvg(svg))
  })

  it('keeps references inside the SVG, and its styles without outside urls', () => {
    const svg = sanitizeSvg(
      '<svg xmlns="http://www.w3.org/2000/svg"><style>@import url(https://evil.example/a.css); .a { fill: url(#grad); background: url(https://evil.example/p.png) }</style><defs><marker id="arrow"/></defs><path marker-end="url(#arrow)" style="fill: url(http://evil.example/x)"/><use href="#arrow"/></svg>',
    )
    expect(svg).toContain('url(#grad)')
    expect(svg).toContain('href="#arrow"')
    expect(svg).not.toContain('evil.example')
    expect(svg).not.toContain('@import')
  })

  it('cleans CSS', () => {
    expect(cleanCss('a{b:url("https://x/y")} c{d:url(#e)} @import "f.css";')).toBe('a{b:none} c{d:url(#e)} ')
  })
})

describe('drawing', () => {
  const diagrams = [
    'flowchart LR\n  a["<img src=x onerror=alert(1)>"] --> b\n  click a "javascript:alert(1)"\n  click b href "javascript:alert(2)"',
    'flowchart LR\n  a["<b onclick=\'alert(1)\'>bold</b>"] --> b["<script>alert(1)</script>"]',
    '%%{init: {"securityLevel": "loose", "htmlLabels": true}}%%\nflowchart LR\n  a["<a href=javascript:alert(1)>x</a>"] --> b\n  click a call alert(1)',
    '%%{init: {"themeCSS": "* { background: url(https://evil.example/p.png) }"}}%%\nflowchart LR\n  a --> b',
    'sequenceDiagram\n  Alice->>Bob: <img src=x onerror=alert(1)>\n  link Alice: Dashboard @ javascript:alert(1)',
  ]

  it.each(diagrams)('draws without anything executable: %s', async (source) => {
    const result = await drawDiagram(source, lightTheme)
    expect(result.ok).toBe(true)
    if (result.ok) {
      assertClean(result.svg)
      expect(result.svg).not.toContain('evil.example')
    }
  })

  it('draws labels as SVG text, the HTML in them as text', async () => {
    const result = await drawDiagram('flowchart LR\n  a["<b>bold</b>"] --> b', lightTheme)
    expect(result.ok).toBe(true)
    if (result.ok) {
      expect(result.svg).not.toMatch(/<foreignObject/i)
      expect(result.svg).not.toMatch(/<b>/)
    }
  })

  it.each([
    ['journey', 'journey\n  title A day\n  section Morning\n    Coffee: 5: Me', 'Coffee'],
    ['timeline', 'timeline\n  2019 : opened\n  2026 : uplink', 'uplink'],
  ])('draws a %s with its texts as SVG text, not in a foreignObject', async (_type, source, text) => {
    const result = await drawDiagram(source, lightTheme)
    expect(result.ok).toBe(true)
    if (result.ok) {
      expect(result.svg).not.toMatch(/foreignObject/i)
      expect(result.svg).toContain(text)
    }
  })

  it('shows a syntax error with its message', async () => {
    const result = await drawDiagram('flowchart LR\n  a -->', lightTheme)
    expect(result.ok).toBe(false)
    expect(!result.ok && result.message).toMatch(/error|expect/i)
  })

  it('refuses a text above the limit as too large', async () => {
    const result = await drawDiagram(`flowchart LR\n${'  a --> b\n'.repeat(5100)}`, lightTheme)
    expect(result).toMatchObject({ ok: false, tooLarge: true })
  })
})
