import type { Element, ElementContent, Properties, Root } from 'hast'
import { defineAsyncComponent, defineComponent, h, type PropType, type VNodeChild } from 'vue'
import { RouterLink } from 'vue-router'
import AttachmentChip from './AttachmentChip.vue'
import CodeBlock from './CodeBlock.vue'
import MarkdownImage from './MarkdownImage.vue'
import { fileUrl, maxAutoDiagrams } from './renderMarkdown'
import type { MarkdownFile } from './types'

// Mermaid's component (and with it Mermaid) only for pages that have a diagram (§5.8).
const MermaidDiagram = defineAsyncComponent(() => import('./MermaidDiagram.vue'))

/** A hast property as a DOM attribute: className → class, ariaLabel → aria-label, colSpan → colspan. */
function attributeName(key: string): string {
  if (key === 'className') {
    return 'class'
  }
  if (key === 'htmlFor') {
    return 'for'
  }
  if (/^(aria|data)[A-Z]/.test(key)) {
    return key.replace(/[A-Z]/g, (c) => `-${c.toLowerCase()}`)
  }
  return key.toLowerCase()
}

function attributes(properties: Properties): Record<string, unknown> {
  const out: Record<string, unknown> = {}
  for (const [key, value] of Object.entries(properties)) {
    if (value === null || value === undefined || value === false) {
      continue
    }
    out[attributeName(key)] = Array.isArray(value) ? value.join(' ') : value
  }
  return out
}

/** The source line a top-level block starts on, for the code editor's preview to scroll with (§5.3). */
function lineOf(node: Element): number | undefined {
  if (node.position) {
    return node.position.start.line
  }
  for (const child of node.children) {
    if (child.type === 'element') {
      const line = lineOf(child)
      if (line) {
        return line
      }
    }
  }
  return undefined
}

interface Context {
  files: Map<string, MarkdownFile> | null
}

function render(nodes: ElementContent[], context: Context, top = false): VNodeChild[] {
  return nodes.map((node) => {
    if (node.type === 'text') {
      return node.value
    }
    if (node.type !== 'element') {
      return null
    }
    const props = node.properties
    const children = () => render(node.children, context)
    const line = top ? lineOf(node) : undefined
    switch (node.tagName) {
      case 'pnmt-mermaid':
        return h(MermaidDiagram, {
          source: String(props.source ?? ''),
          auto: Number(props.index ?? 0) < maxAutoDiagrams,
          'data-line': line,
        })
      case 'pnmt-code':
        return h(
          CodeBlock,
          { source: String(props.source ?? ''), language: String(props.language ?? ''), 'data-line': line },
          { default: children },
        )
      case 'pnmt-attachment': {
        const id = String(props.fileId ?? '')
        const file = context.files?.get(id) ?? null
        return h(AttachmentChip, {
          href: String(props.href ?? ''),
          name: String(props.name ?? ''),
          file,
          removed: context.files !== null && !file,
        })
      }
      case 'pnmt-link':
        return h(RouterLink, { to: String(props.to ?? '/') }, { default: children })
      case 'img':
        if (typeof props.src === 'string' && fileUrl.test(props.src)) {
          return h(MarkdownImage, {
            src: props.src,
            alt: typeof props.alt === 'string' ? props.alt : undefined,
            title: typeof props.title === 'string' ? props.title : undefined,
          })
        }
        return h('img', attributes(props))
      default:
        return h(node.tagName, { ...attributes(props), 'data-line': line }, render(node.children, context))
    }
  })
}

/**
 * The sanitised tree of renderMarkdown as Vue nodes (docs/locations-knowledge-plan.md §5.6): no HTML
 * string reaches the page. PNMT's own elements become their components.
 */
export default defineComponent({
  name: 'MarkdownContent',
  props: {
    tree: { type: Object as PropType<Root>, required: true },
    files: { type: Array as PropType<readonly MarkdownFile[] | null>, default: null },
  },
  setup(props) {
    return () => {
      const files = props.files ? new Map(props.files.map((f) => [f.id, f])) : null
      return render(props.tree.children as ElementContent[], { files }, true)
    }
  },
})
