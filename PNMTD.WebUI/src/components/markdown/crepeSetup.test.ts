import { readdirSync, readFileSync, statSync, writeFileSync } from 'node:fs'
import { join, relative } from 'node:path'
import { afterAll, beforeAll, describe, expect, it, vi } from 'vitest'
import { createVisualEditor, visualFeatures, type VisualEditorHandle } from './crepeSetup'
import { rewriteSummary } from './lineDiff'
import { renderMarkdown } from './renderMarkdown'
import { roundTripCorpus } from './roundTrip.fixture'

// jsdom has neither observer; Crepe's code blocks and image blocks create them.
class NoObserver {
  observe() {}
  unobserve() {}
  disconnect() {}
  takeRecords() {
    return []
  }
}

beforeAll(() => {
  vi.stubGlobal('IntersectionObserver', NoObserver)
  vi.stubGlobal('ResizeObserver', NoObserver)
})

async function open(markdown: string): Promise<{ handle: VisualEditorHandle; root: HTMLElement }> {
  const root = document.createElement('div')
  document.body.appendChild(root)
  const handle = await createVisualEditor({
    root,
    markdown,
    placeholder: '',
    upload: null,
    pickPage: null,
    renderDiagram: () => {},
    onDocChange: () => {},
    notice: () => {},
  })
  return { handle, root }
}

function sources(dir: string): string[] {
  return readdirSync(dir).flatMap((name) => {
    const path = join(dir, name)
    if (statSync(path).isDirectory()) {
      return sources(path)
    }
    return /\.(ts|vue)$/.test(name) && !/\.test\.ts$/.test(name) ? [path] : []
  })
}

// docs/locations-knowledge-plan.md §5.2 and §12: Crepe's AI feature is never configured. It would
// send a page's text past PNMT's pipeline and the sensitive rule.
describe("Crepe's AI feature", () => {
  it('is not among the features PNMT adds', () => {
    expect(visualFeatures).not.toContain('ai')
    expect(visualFeatures).not.toContain('latex')
  })

  it('is never imported or configured anywhere in the web UI', () => {
    const src = join(__dirname, '..', '..')
    for (const file of sources(src)) {
      const text = readFileSync(file, 'utf8')
      const where = relative(src, file)
      expect(text, where).not.toMatch(/@milkdown\/crepe\/feature\/(ai|latex)/)
      expect(text, where).not.toMatch(/@milkdown\/crepe\/llm-providers/)
      expect(text, where).not.toMatch(/CrepeFeature\.AI|Crepe\.Feature\.AI|aiProviderConfig|useAIProviderConfig/)
      // The Crepe class turns features on by default and loads every one's code (AI's and KaTeX's);
      // PNMT builds its editor feature by feature with CrepeBuilder.
      expect(text, where).not.toMatch(/from '@milkdown\/crepe'(?!\/)/)
      expect(text, where).not.toMatch(/new Crepe\(/)
    }
  })

  it('is not among the features of the editor PNMT creates, and has no provider', async () => {
    const { handle } = await open('# Hello')
    handle.editor.action((ctx) => {
      const features = ctx.get<string[], 'FeaturesCtx'>('FeaturesCtx')
      expect([...features].sort()).toEqual([...visualFeatures].sort())
      expect(ctx.isInjected('aiProviderConfig')).toBe(false)
    })
    await handle.destroy()
  })
})

describe('the visual editor', () => {
  it('starts with the features of §5.2, and AI and LaTeX off', async () => {
    const { handle, root } = await open('# Title\n\n| a |\n| - |\n| 1 |\n\n```bash\necho hi\n```\n')
    expect(root.querySelector('.milkdown-top-bar')).not.toBeNull()
    expect(root.querySelector('.milkdown-table-block, table')).not.toBeNull()
    expect(root.querySelector('.milkdown-code-block')).not.toBeNull()
    expect(root.innerHTML).not.toMatch(/katex|ai-instruction|Ask AI/i)
    await handle.destroy()
  })

  it('names the top bar’s buttons, PNMT’s Attach file among them', async () => {
    const { handle, root } = await open('text')
    const labels = Array.from(root.querySelectorAll('.milkdown-top-bar button.top-bar-item')).map((b) =>
      b.getAttribute('aria-label'),
    )
    expect(labels).toContain('Bold (Ctrl/⌘ B)')
    expect(labels).toContain('Attach file')
    expect(labels).not.toContain(null)
    await handle.destroy()
  })

  it("keeps an image's alt text and title through a round trip", async () => {
    const markdown = '![Cage B, front](/api/v1/files/0193/cage-b.png "Photo")\n'
    const { handle } = await open(markdown)
    expect(handle.markdown()).toBe(markdown)
    await handle.destroy()
  })
})

// K0: the round trip of the corpus through Crepe, the lines it rewrites counted (§5.4, §13).
describe('the round trip through Crepe', () => {
  const results: { name: string; lines: number; total: number; same: boolean; example: string }[] = []

  afterAll(() => {
    const rewritten = results.filter((r) => r.lines > 0)
    const report = [
      `Round trip of ${results.length} documents through Crepe: ${rewritten.length} rewritten, ` +
        `${results.reduce((n, r) => n + r.lines, 0)} of ${results.reduce((n, r) => n + r.total, 0)} lines.`,
      ...results.map(
        (r) =>
          `${String(r.lines).padStart(3)} / ${String(r.total).padStart(3)}  ${r.same ? '        ' : 'MEANING '}${r.name}${r.example ? `   e.g. ${r.example}` : ''}`,
      ),
    ].join('\n')
    if (process.env.PNMT_ROUND_TRIP_REPORT) {
      writeFileSync(process.env.PNMT_ROUND_TRIP_REPORT, `${report}\n`)
    }
  })

  // What the reader sees, as text: the same before and after, or the rewrite changed the meaning.
  const shown = (markdown: string) => {
    const box = document.createElement('div')
    const walk = (node: ReturnType<typeof renderMarkdown>['children'][number]): string =>
      node.type === 'text' ? node.value : 'children' in node ? node.children.map(walk).join('') : ''
    box.textContent = renderMarkdown(markdown).children.map(walk).join('')
    return (box.textContent ?? '').replace(/\s+/g, ' ').trim()
  }

  it.each(roundTripCorpus)('$name', async ({ name, markdown }) => {
    const { handle } = await open(markdown)
    const written = handle.markdown()
    await handle.destroy()
    const summary = rewriteSummary(markdown, written)
    const same = shown(markdown) === shown(written)
    results.push({
      name,
      lines: summary.lines,
      total: markdown.split('\n').length - 1,
      same,
      example: summary.example ? `${JSON.stringify(summary.example.from)} → ${JSON.stringify(summary.example.to)}` : '',
    })
    // A rewrite may change how the text is written, never what it says.
    expect(shown(written)).toBe(shown(markdown))
  })
})
