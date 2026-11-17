<script lang="ts">
import type { ElementContent } from 'hast'
import { computed, defineComponent, h, type VNodeChild } from 'vue'
import { highlightJson } from './highlightJson'

/** The highlighted spans as Vue nodes: their classes only, every other property dropped; text stays text. */
function render(nodes: ElementContent[]): VNodeChild[] {
  return nodes.map((node) => {
    if (node.type === 'text') return node.value
    if (node.type !== 'element') return null
    const className = node.properties.className
    return h('span', { class: Array.isArray(className) ? className.join(' ') : undefined }, render(node.children))
  })
}

/**
 * JSON text, highlighted (docs/advanced-sensor-plan.md §10.3 *Raw*, §13). Past `limit` characters it stays plain, which
 * keeps a 256 KiB report quick.
 */
export default defineComponent({
  name: 'JsonHighlight',
  props: {
    text: { type: String, required: true },
    limit: { type: Number, default: 200_000 },
  },
  setup(props) {
    const content = computed<VNodeChild[]>(() => {
      if (props.text.length > props.limit) return [props.text]
      const pre = highlightJson(props.text).children[0]
      const code = pre?.type === 'element' ? pre.children[0] : undefined
      return code?.type === 'element' ? render(code.children) : [props.text]
    })
    return () => h('pre', { class: 'json-highlight' }, [h('code', { class: 'hljs' }, content.value)])
  },
})
</script>

<!-- Not scoped: the spans are made by the render function. highlight.js's classes in PNMT's colours, as the knowledge
     base's code blocks (light; dark below). -->
<style>
.json-highlight {
  margin: 0;
  max-height: 40rem;
  overflow: auto;
  font-family: var(--pnmt-mono);
  font-size: 0.857rem;
  background: var(--pnmt-bg);
  border: 1px solid var(--pnmt-border);
  border-radius: var(--p-border-radius-md);
  padding: var(--pnmt-space-2) var(--pnmt-space-3);
}

.json-highlight code {
  font-size: inherit;
}

.json-highlight .hljs-attr {
  color: var(--p-sky-700);
}

.json-highlight .hljs-string {
  color: var(--p-green-700);
}

.json-highlight :is(.hljs-number, .hljs-literal) {
  color: var(--p-orange-700);
}

:root.pnmt-dark .json-highlight .hljs-attr {
  color: var(--p-sky-300);
}

:root.pnmt-dark .json-highlight .hljs-string {
  color: var(--p-green-300);
}

:root.pnmt-dark .json-highlight :is(.hljs-number, .hljs-literal) {
  color: var(--p-orange-300);
}
</style>
