<script setup lang="ts">
import { computed } from 'vue'
import MarkdownContent from './MarkdownContent'
import { clobberPrefix, renderMarkdown } from './renderMarkdown'
import type { MarkdownFile } from './types'

/**
 * Markdown, rendered for reading (docs/locations-knowledge-plan.md §5.6): pages, location fields, a
 * revision, the editors' previews. The rules are renderMarkdown's; this is the look, and in-page
 * #anchors scrolled to without leaving the route.
 *
 * `files`: the metadata of the files the text uses, from the page's response. With it, attachment
 * chips show their size, and a file that isn't among them shows as removed; without it (null), chips
 * show their link's text.
 */
const props = withDefaults(defineProps<{ source: string; files?: readonly MarkdownFile[] | null }>(), {
  files: null,
})

const tree = computed(() => renderMarkdown(props.source))

// A plain #anchor would go through the router, whose scrollBehavior starts the page at its top.
function onClick(event: MouseEvent) {
  const link = (event.target as HTMLElement | null)?.closest?.('a[href^="#"]')
  if (!link || event.defaultPrevented || event.button !== 0 || event.metaKey || event.ctrlKey) {
    return
  }
  const id = decodeURIComponent(link.getAttribute('href')!.slice(1))
  const target = document.getElementById(id) ?? document.getElementById(clobberPrefix + id)
  if (target) {
    event.preventDefault()
    target.scrollIntoView({ block: 'start' })
    history.replaceState(history.state, '', `#${target.id}`)
  }
}
</script>

<template>
  <div class="pnmt-markdown" @click="onClick">
    <MarkdownContent :tree="tree" :files="files" />
  </div>
</template>

<style>
/* Markdown as read (§5.6). Not scoped: the content is built from the tree, and the editors' previews share it. */
.pnmt-markdown {
  overflow-wrap: anywhere;
  line-height: 1.55;
}

.pnmt-markdown > :first-child {
  margin-top: 0;
}

.pnmt-markdown > :last-child {
  margin-bottom: 0;
}

.pnmt-markdown :is(p, ul, ol, blockquote, pre, .md-table, .md-code, hr) {
  margin: 0 0 0.85em;
}

.pnmt-markdown :is(h1, h2, h3, h4, h5, h6) {
  margin: 1.2em 0 0.5em;
  line-height: 1.3;
  font-weight: 600;
  scroll-margin-top: calc(var(--pnmt-header-height) + var(--pnmt-space-2));
}

.pnmt-markdown h1 {
  font-size: 1.5rem;
}

.pnmt-markdown h2 {
  font-size: 1.3rem;
  padding-bottom: 0.2em;
  border-bottom: 1px solid var(--pnmt-border);
}

.pnmt-markdown h3 {
  font-size: 1.15rem;
}

.pnmt-markdown :is(h4, h5, h6) {
  font-size: 1rem;
}

.pnmt-markdown :is(ul, ol) {
  padding-left: 1.6em;
}

.pnmt-markdown li + li,
.pnmt-markdown li > :is(ul, ol) {
  margin-top: 0.2em;
}

.pnmt-markdown li > :is(ul, ol) {
  margin-bottom: 0;
}

.pnmt-markdown .contains-task-list {
  list-style: none;
  padding-left: 0.4em;
}

.pnmt-markdown .contains-task-list .contains-task-list {
  padding-left: 1.4em;
}

.pnmt-markdown .task-list-item input {
  margin: 0 0.45em 0 0;
  vertical-align: -0.1em;
}

.pnmt-markdown blockquote {
  padding: 0 0 0 1em;
  border-left: 3px solid var(--pnmt-border);
  color: var(--pnmt-muted);
}

.pnmt-markdown hr {
  border: none;
  border-top: 1px solid var(--pnmt-border);
}

.pnmt-markdown img {
  max-width: 100%;
  height: auto;
  border-radius: var(--p-border-radius-sm);
}

.pnmt-markdown a.md-image {
  display: inline-block;
  cursor: zoom-in;
}

.pnmt-markdown .md-external-image {
  color: var(--pnmt-muted);
  font-style: italic;
}

.pnmt-markdown :not(pre) > code {
  padding: 0.1em 0.35em;
  border-radius: var(--p-border-radius-sm);
  background: var(--pnmt-bg);
  border: 1px solid var(--pnmt-border);
}

.pnmt-markdown pre {
  padding: var(--pnmt-space-3);
  border: 1px solid var(--pnmt-border);
  border-radius: var(--p-border-radius-md);
  background: var(--pnmt-bg);
  overflow-x: auto;
  line-height: 1.45;
}

.pnmt-markdown pre code {
  font-family: var(--pnmt-mono);
  font-size: 0.9286rem;
  white-space: pre;
}

.pnmt-markdown .md-code > pre {
  margin: 0;
}

.pnmt-markdown .md-table {
  overflow-x: auto;
}

.pnmt-markdown table {
  border-collapse: collapse;
  min-width: 50%;
}

.pnmt-markdown :is(th, td) {
  padding: var(--pnmt-space-1) var(--pnmt-space-3);
  border: 1px solid var(--pnmt-border);
  vertical-align: top;
}

.pnmt-markdown th {
  background: var(--pnmt-bg);
  font-weight: 600;
  text-align: left;
}

.pnmt-markdown th[align='center'],
.pnmt-markdown td[align='center'] {
  text-align: center;
}

.pnmt-markdown th[align='right'],
.pnmt-markdown td[align='right'] {
  text-align: right;
}

.pnmt-markdown section.footnotes {
  margin-top: 2em;
  font-size: 0.9286rem;
  color: var(--pnmt-muted);
}

.pnmt-markdown .sr-only {
  position: absolute;
  width: 1px;
  height: 1px;
  overflow: hidden;
  clip: rect(0 0 0 0);
}

/* highlight.js's classes in PNMT's colours (light; dark below). */
.pnmt-markdown .hljs-comment,
.pnmt-markdown .hljs-quote,
.pnmt-markdown .hljs-meta {
  color: var(--p-surface-500);
  font-style: italic;
}

.pnmt-markdown :is(.hljs-keyword, .hljs-selector-tag, .hljs-built_in, .hljs-name, .hljs-section) {
  color: var(--p-purple-700);
}

.pnmt-markdown :is(.hljs-string, .hljs-attr-value, .hljs-template-variable, .hljs-addition) {
  color: var(--p-green-700);
}

.pnmt-markdown :is(.hljs-number, .hljs-literal, .hljs-symbol, .hljs-bullet) {
  color: var(--p-orange-700);
}

.pnmt-markdown :is(.hljs-attr, .hljs-attribute, .hljs-variable, .hljs-property, .hljs-params) {
  color: var(--p-sky-700);
}

.pnmt-markdown :is(.hljs-title, .hljs-type, .hljs-function, .hljs-class) {
  color: var(--p-blue-700);
}

.pnmt-markdown .hljs-deletion {
  color: var(--p-red-700);
}

:root.pnmt-dark .pnmt-markdown :is(.hljs-keyword, .hljs-selector-tag, .hljs-built_in, .hljs-name, .hljs-section) {
  color: var(--p-purple-300);
}

:root.pnmt-dark .pnmt-markdown :is(.hljs-string, .hljs-attr-value, .hljs-template-variable, .hljs-addition) {
  color: var(--p-green-300);
}

:root.pnmt-dark .pnmt-markdown :is(.hljs-number, .hljs-literal, .hljs-symbol, .hljs-bullet) {
  color: var(--p-orange-300);
}

:root.pnmt-dark .pnmt-markdown :is(.hljs-attr, .hljs-attribute, .hljs-variable, .hljs-property, .hljs-params) {
  color: var(--p-sky-300);
}

:root.pnmt-dark .pnmt-markdown :is(.hljs-title, .hljs-type, .hljs-function, .hljs-class) {
  color: var(--p-blue-300);
}

:root.pnmt-dark .pnmt-markdown .hljs-deletion {
  color: var(--p-red-300);
}

/* Printing (§5.6): a page or location prints cleanly, links show where they go. */
@media print {
  .pnmt-markdown {
    color: #000;
  }

  .pnmt-markdown a[href^='http']::after {
    content: ' (' attr(href) ')';
    font-size: 0.85em;
    color: #444;
  }

  .pnmt-markdown :is(pre, .md-table, .md-code, img, blockquote) {
    break-inside: avoid;
  }

  .pnmt-markdown pre,
  .pnmt-markdown pre code {
    white-space: pre-wrap;
  }

  .pnmt-markdown .md-table {
    overflow: visible;
  }
}
</style>
