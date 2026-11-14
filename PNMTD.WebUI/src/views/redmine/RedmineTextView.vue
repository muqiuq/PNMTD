<script setup lang="ts">
import { computed } from 'vue'
import type { S } from '@/api/resources'
import MarkdownView from '@/components/markdown/MarkdownView.vue'

/**
 * A text of Redmine's (§6.8): Markdown through PNMT's MarkdownView (the server made Redmine's own references links), or
 * plain text with its line breaks, and URLs and #1234 as links. Ticket text is untrusted: nothing of it is HTML.
 */
const props = defineProps<{ text: S['RedmineText'] }>()

type Part =
  { kind: 'text'; value: string } | { kind: 'url'; value: string } | { kind: 'ticket'; value: string; id: number }
const parts = computed<Part[]>(() => {
  if (props.text.format === 'markdown') return []
  const out: Part[] = []
  const re = /(https?:\/\/[^\s<>"']+[^\s<>"'.,;:!?)\]])|(?<![\w&])#(\d+)\b/g
  let last = 0
  for (const m of props.text.text.matchAll(re)) {
    if (m.index > last) out.push({ kind: 'text', value: props.text.text.slice(last, m.index) })
    out.push(m[1] ? { kind: 'url', value: m[1] } : { kind: 'ticket', value: m[0], id: Number(m[2]) })
    last = m.index + m[0].length
  }
  if (last < props.text.text.length) out.push({ kind: 'text', value: props.text.text.slice(last) })
  return out
})
</script>

<template>
  <MarkdownView v-if="text.format === 'markdown'" :source="text.text" />
  <p v-else class="plain">
    <template v-for="(p, i) in parts" :key="i">
      <a v-if="p.kind === 'url'" :href="p.value" target="_blank" rel="noopener noreferrer nofollow">{{ p.value }}</a>
      <RouterLink v-else-if="p.kind === 'ticket'" :to="{ name: 'redmine-issue', params: { id: p.id } }">{{
        p.value
      }}</RouterLink>
      <template v-else>{{ p.value }}</template>
    </template>
  </p>
</template>

<style scoped>
.plain {
  white-space: pre-wrap;
  overflow-wrap: anywhere;
  margin: 0;
}
</style>
