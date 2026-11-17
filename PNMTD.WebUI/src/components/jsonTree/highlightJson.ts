import type { Root } from 'hast'
import json from 'highlight.js/lib/languages/json'
import rehypeHighlight from 'rehype-highlight'
import { unified } from 'unified'

/**
 * JSON highlighted as the knowledge base's code blocks are (docs/advanced-sensor-plan.md §10.3 *Raw*, §13): rehype-highlight
 * over a hast tree of `pre > code`. JsonHighlight turns the tree into Vue nodes, so no HTML string is ever made.
 */
const processor = unified().use(rehypeHighlight, { languages: { json }, detect: false })

export function highlightJson(text: string): Root {
  const tree: Root = {
    type: 'root',
    children: [
      {
        type: 'element',
        tagName: 'pre',
        properties: {},
        children: [
          {
            type: 'element',
            tagName: 'code',
            properties: { className: ['language-json'] },
            children: [{ type: 'text', value: text }],
          },
        ],
      },
    ],
  }
  return processor.runSync(tree) as Root
}
