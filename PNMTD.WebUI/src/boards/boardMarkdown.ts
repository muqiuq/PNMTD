import type { Element, ElementContent, Root } from 'hast'
import rehypeSanitize, { defaultSchema, type Options as SanitizeSchema } from 'rehype-sanitize'
import remarkGfm from 'remark-gfm'
import remarkParse from 'remark-parse'
import remarkRehype from 'remark-rehype'
import { unified } from 'unified'
import { h, type VNodeChild } from 'vue'

/**
 * A note's Markdown on a board (docs/boards-plan.md §7.1, §14): GFM, sanitised, without images or raw HTML; links open in a new tab and
 * pass no referrer. A small renderer of its own, so that the board app carries no highlighter, diagrams or editor.
 */
const schema: SanitizeSchema = {
  ...defaultSchema,
  tagNames: (defaultSchema.tagNames ?? []).filter(
    (t) => !['img', 'picture', 'source', 'video', 'audio', 'iframe', 'input'].includes(t),
  ),
}

const processor = unified().use(remarkParse).use(remarkGfm).use(remarkRehype).use(rehypeSanitize, schema)

export function noteTree(markdown: string): Root {
  return processor.runSync(processor.parse(markdown)) as Root
}

function attributes(element: Element): Record<string, unknown> {
  const out: Record<string, unknown> = {}
  for (const [key, value] of Object.entries(element.properties ?? {})) {
    if (value === null || value === undefined || value === false) continue
    const name = key === 'className' ? 'class' : key.toLowerCase()
    out[name] = Array.isArray(value) ? value.join(' ') : value
  }
  if (element.tagName === 'a') {
    out.target = '_blank'
    out.rel = 'noopener noreferrer'
  }
  return out
}

function nodes(children: ElementContent[]): VNodeChild[] {
  return children.map((child) => {
    if (child.type === 'text') return child.value
    if (child.type === 'element') return h(child.tagName, attributes(child), nodes(child.children))
    return null
  })
}

/** The note as Vue nodes: no HTML string ever reaches the page. */
export function noteNodes(markdown: string): VNodeChild[] {
  return nodes(noteTree(markdown).children as ElementContent[])
}
