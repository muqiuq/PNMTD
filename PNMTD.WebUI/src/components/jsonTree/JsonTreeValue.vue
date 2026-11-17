<script setup lang="ts">
import { computed, inject, ref } from 'vue'
import { formatAbsolute, formatRelative } from '@/format'
import { formatPath, healthTone, looksLikeTime, parseTime, type Segment } from '@/views/sensors/advanced/advanced'
import JsonTreeMarks from './JsonTreeMarks.vue'
import { isContainer, jsonTreeKey, scalarText, summaryOf } from './jsonTree'

/**
 * One value of a report (docs/advanced-sensor-plan.md §10.3), readable as it is: a time in the browser's zone with how long
 * ago, the original on hover; ✓ and ✗ for booleans; null muted; health words in their colour; long or multi-line text as a
 * block, cut past 10 lines. Always text bindings: what the app sends never becomes HTML, and links stay text (§13).
 */
const props = defineProps<{ value: unknown; segments: Segment[]; unit?: unknown }>()
const ctx = inject(jsonTreeKey)!

const BLOCK_LENGTH = 120
const MAX_LINES = 10
const MAX_CHARACTERS = 1_000

const path = computed(() => formatPath(props.segments))
const text = computed(() => (typeof props.value === 'string' ? props.value : null))
const time = computed(() => (text.value !== null && looksLikeTime(text.value) ? parseTime(text.value) : null))
const tone = computed(() => (text.value !== null ? healthTone(text.value) : null))
const lines = computed(() => (text.value ?? '').split('\n'))
const block = computed(() => text.value !== null && (lines.value.length > 1 || text.value.length > BLOCK_LENGTH))
const long = computed(() => lines.value.length > MAX_LINES || (text.value?.length ?? 0) > MAX_CHARACTERS)
const expanded = ref(false)
const shown = computed(() => {
  const t = text.value ?? ''
  if (expanded.value || !long.value) return t
  const head = lines.value.slice(0, MAX_LINES).join('\n')
  return head.length > MAX_CHARACTERS ? `${head.slice(0, MAX_CHARACTERS)}…` : `${head}…`
})
const change = computed(() => {
  const c = ctx.changes.value
  if (!c || !c.changed.has(path.value)) return null
  const before = c.changed.get(path.value)
  return before === undefined
    ? 'New since the previous report'
    : `Before: ${isContainer(before) ? summaryOf(before) : scalarText(before)}`
})
const unitText = computed(() =>
  props.unit === undefined || props.unit === null || props.unit === '' ? null : scalarText(props.unit),
)
</script>

<template>
  <span class="jt-value" :class="{ 'jt-changed': change, 'jt-has-block': block }" :title="change ?? undefined">
    <span v-if="value === null" class="jt-null">null</span>
    <span v-else-if="value === true" class="jt-bool tone-ok" title="true"
      ><span aria-hidden="true">✓</span><span class="jt-sr">true</span></span
    >
    <span v-else-if="value === false" class="jt-bool jt-false" title="false"
      ><span aria-hidden="true">✗</span><span class="jt-sr">false</span></span
    >
    <span v-else-if="typeof value === 'number'" class="jt-number">{{ value }}</span>
    <span v-else-if="isContainer(value)" class="jt-null">{{ summaryOf(value) }}</span>
    <span v-else-if="time" class="jt-time"
      ><time :datetime="time.toISOString()" :title="text ?? undefined">{{ formatAbsolute(time) }}</time>
      <span class="jt-ago">{{ formatRelative(time, ctx.now.value) }}</span></span
    >
    <span v-else-if="tone" class="jt-health" :class="`tone-${tone}`">{{ text }}</span>
    <span v-else-if="block" class="jt-block">
      <span class="jt-text">{{ shown }}</span>
      <button v-if="long" type="button" class="jt-link" :aria-expanded="expanded" @click="expanded = !expanded">
        {{ expanded ? 'Show less' : lines.length > 1 ? `Show all ${lines.length} lines` : 'Show all' }}
      </button>
    </span>
    <span v-else-if="text === ''" class="jt-null">""</span>
    <span v-else class="jt-string">{{ text }}</span>
    <span v-if="unitText" class="jt-unit">{{ unitText }}</span>
    <span v-if="change" class="jt-sr">({{ change }})</span>
    <JsonTreeMarks :segments="segments" :value="value" />
  </span>
</template>
